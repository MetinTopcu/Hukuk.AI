using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hukuk.AI.Data;
using Hukuk.AI.Data.Entities;
using Hukuk.AI.Ingestion.Chunking;
using Hukuk.AI.Ingestion.Embedding;
using Hukuk.AI.Ingestion.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.ML.Tokenizers;
using Microsoft.SemanticKernel;

// Adım 1: data/raw/*.html -> data/parsed/*.json (bilgi tabanına girecek maddeler)
// Adım 2: parse edilen maddeler -> data/chunks/{A,B,C,D}.json (karşılaştırılacak chunk stratejileri)
// Adım 3+4 (sadece "dotnet run -- embed" ile, ücretli API çağrısı): chunk'lar embed edilip knowledge_chunks'a yazılır

var dataDir = Path.Combine(FindRepoRoot(), "data");

var laws = new[]
{
    // Bilgi tabanına sadece TBK kira maddeleri (299-378) giriyor.
    new LawSource("6098", "Türk Borçlar Kanunu", a => a.ArticleType == ArticleType.Asil && a.ArticleNo is >= 299 and <= 378),
    new LawSource("4857", "İş Kanunu", _ => true),
};

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // Türkçe karakterler okunabilir kalsın
    Converters = { new JsonStringEnumConverter() },
};

Directory.CreateDirectory(Path.Combine(dataDir, "parsed"));

var keptByLaw = new Dictionary<string, List<ParsedArticle>>();

foreach (var law in laws)
{
    var html = File.ReadAllText(Path.Combine(dataDir, "raw", $"{law.Law}.html"));
    var all = MevzuatParser.Parse(law.Law, html);

    var duplicates = all.GroupBy(a => (a.ArticleType, a.ArticleNo)).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
    var inScope = all.Where(law.Filter).ToList();
    var kept = inScope.Where(a => !a.HasNoText).ToList();
    keptByLaw[law.Law] = kept;

    var output = new { law.Law, law.LawName, Count = kept.Count, Articles = kept };
    var outPath = Path.Combine(dataDir, "parsed", $"{law.Law}.json");
    File.WriteAllText(outPath, JsonSerializer.Serialize(output, jsonOptions));

    Console.WriteLine($"{law.Law} {law.LawName}: toplam {all.Count} madde, kapsamda {inScope.Count}, " +
                      $"metinsiz (mülga/işlenmiş) {inScope.Count - kept.Count}, yazılan {kept.Count} -> {outPath}");
    foreach (var d in duplicates)
        Console.WriteLine($"  UYARI: tekrar eden madde {d.ArticleType} {d.ArticleNo}");
}

// Embedding modeliyle aynı tokenizer (cl100k_base), token sayıları birebir tutsun.
var tokenizer = TiktokenTokenizer.CreateForModel("text-embedding-3-large");
var lawNames = laws.ToDictionary(l => l.Law, l => l.LawName);

IChunker[] chunkers =
[
    new ArticleChunker(tokenizer),
    new FixedTokenChunker(tokenizer),
    new ContextualArticleChunker(tokenizer, lawNames),
    new HybridArticleChunker(tokenizer, lawNames),
];

Directory.CreateDirectory(Path.Combine(dataDir, "chunks"));

var chunksByStrategy = new Dictionary<string, List<Chunk>>();

foreach (var chunker in chunkers)
{
    var chunks = chunksByStrategy[chunker.Name] = keptByLaw.SelectMany(kv => chunker.Split(kv.Key, kv.Value)).ToList();
    var outPath = Path.Combine(dataDir, "chunks", $"{chunker.Name}.json");
    File.WriteAllText(outPath, JsonSerializer.Serialize(new { Strategy = chunker.Name, Count = chunks.Count, Chunks = chunks }, jsonOptions));

    var tokens = chunks.Select(c => c.TokenCount).ToList();
    Console.WriteLine($"Strateji {chunker.Name}: {chunks.Count} chunk, token min {tokens.Min()} / ort {tokens.Average():F0} / max {tokens.Max()} -> {outPath}");
}

if (!args.Contains("embed"))
    return;

var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
string Required(string key) => configuration[key] ?? throw new InvalidOperationException($"{key} ayarı bulunamadı (user secrets).");

// SK'nın embedding generator'ı hâlâ experimental (SKEXP0010) işaretli.
#pragma warning disable SKEXP0010
var kernel = Kernel.CreateBuilder()
    .AddAzureOpenAIEmbeddingGenerator(
        deploymentName: Required("AI:EmbeddingDeploymentName"),
        endpoint: Required("AI:AzureOpenAIEndpoint"),
        apiKey: Required("AI:AzureOpenAIKey"),
        dimensions: 1536) // model varsayılanı 3072; kolon vector(1536)
    .Build();
#pragma warning restore SKEXP0010
var generator = kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseHukukAiPostgres(Required("ConnectionStrings:DefaultConnection")).Options;
await using var db = new AppDbContext(dbOptions);
var embedder = new ChunkEmbedder(generator, db, lawNames, keptByLaw);

var strategies = new Dictionary<string, ChunkStrategy>
{
    ["A"] = ChunkStrategy.A_Madde,
    ["B"] = ChunkStrategy.B_SabitToken,
    ["C"] = ChunkStrategy.C_MaddeBaglamli,
    ["D"] = ChunkStrategy.D_Hibrit,
};

foreach (var (name, chunks) in chunksByStrategy)
{
    var written = await embedder.ReplaceAsync(strategies[name], chunks);
    Console.WriteLine($"Strateji {name}: {written} satır knowledge_chunks'a yazıldı");
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hukuk.AI.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("Hukuk.AI.slnx bulunamadı.");
}

record LawSource(string Law, string LawName, Func<ParsedArticle, bool> Filter);
