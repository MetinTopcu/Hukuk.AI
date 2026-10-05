using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hukuk.AI.Data;
using Hukuk.AI.Data.Entities;
using Hukuk.AI.Evaluation;
using Hukuk.AI.Retrieval;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Pgvector;
using StackExchange.Redis;

// Adım 5: eval setindeki soruları her chunk stratejisinde (A/B/C/D) ve her sorgu yönteminde arar
// (vektör; BM25 kelime araması; ikisinin ağırlıklı RRF ile hybrid'i; soruda madde atfı varsa hybrid'e yönlendirme).
// Sorgu metni her yöntemde "Birlesik": soru + LLM ile kanun diline yeniden yazılmış hali.
// Eşit token bütçesiyle karşılaştırılır: sonuçlar sırayla bütçe dolana kadar alınır (LLM'e gidecek bağlam),
// sonra Recall, Precision ve MRR hesaplanır. "dotnet run -- filtreli" kanun filtreli aramayı da ölçer.
// Soru bazlı detaylar data/eval/results/ altına yazılır.

int[] budgets = [1000, 2000, 4000];
const int fetchCount = 50; // en büyük bütçeyi doldurmaya yetecek kadar aday chunk

var dataDir = Path.Combine(FindRepoRoot(), "data");
var evalSet = EvalSet.Load(Path.Combine(dataDir, "eval", "eval-set-v2.json"));

var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
string Required(string key) => configuration[key] ?? throw new InvalidOperationException($"{key} ayarı bulunamadı (user secrets).");

// Chunk'larla aynı model ve boyut; farklı olursa vektörler karşılaştırılamaz.
#pragma warning disable SKEXP0010
var kernel = Kernel.CreateBuilder()
    .AddAzureOpenAIEmbeddingGenerator(
        deploymentName: Required("AI:EmbeddingDeploymentName"),
        endpoint: Required("AI:AzureOpenAIEndpoint"),
        apiKey: Required("AI:AzureOpenAIKey"),
        dimensions: 1536)
    .AddAzureOpenAIChatCompletion(
        deploymentName: Required("AI:ChatDeploymentName"),
        endpoint: Required("AI:AzureOpenAIEndpoint"),
        apiKey: Required("AI:AzureOpenAIKey"))
    .Build();
#pragma warning restore SKEXP0010
var generator = kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

if (args.Contains("cache"))
{
    await using var redis = await ConnectionMultiplexer.ConnectAsync(Required("ConnectionStrings:Redis"));
    await CacheEval.RunAsync(redis, generator, kernel.GetRequiredService<IChatCompletionService>(), evalSet.Questions, Path.Combine(dataDir, "eval", "cache-pairs-v1.json"), Path.Combine(dataDir, "eval", "results"));
    return;
}

// "dotnet run -- rapor": sentetik sözleşmelerde risk raporu kalitesi (soru eval setine ihtiyaç duymaz).
if (args.Contains("rapor"))
{
    await using var reportDb = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseHukukAiPostgres(Required("ConnectionStrings:DefaultConnection")).Options);
    var hybridCache = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>();
    var search = new KnowledgeSearch(new Retriever(reportDb), new QueryEmbedder(generator, hybridCache, NullLogger<QueryEmbedder>.Instance), NullLogger<KnowledgeSearch>.Instance);
    var reports = new Hukuk.AI.Documents.RiskReportService(search, kernel.GetRequiredService<IChatCompletionService>(), NullLogger<Hukuk.AI.Documents.RiskReportService>.Instance);
    await ReportEval.RunAsync(reports, Path.Combine(dataDir, "eval", "contracts-v1.json"), Path.Combine(dataDir, "eval", "results"), matrix: args.Contains("matris"));
    return;
}

// "dotnet run -- taslak": sözleşme taslağı kalitesi (oturum/Redis yok: çıkarım ve yazım doğrudan çağrılır).
if (args.Contains("taslak"))
{
    await using var draftDb = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseHukukAiPostgres(Required("ConnectionStrings:DefaultConnection")).Options);
    var hybridCache = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>();
    var search = new KnowledgeSearch(new Retriever(draftDb), new QueryEmbedder(generator, hybridCache, NullLogger<QueryEmbedder>.Instance), NullLogger<KnowledgeSearch>.Instance);
    var chat = kernel.GetRequiredService<IChatCompletionService>();
    var reports = new Hukuk.AI.Documents.RiskReportService(search, chat, NullLogger<Hukuk.AI.Documents.RiskReportService>.Instance);
    var writer = new Hukuk.AI.Drafting.DraftWriter(search, reports, chat, NullLogger<Hukuk.AI.Drafting.DraftWriter>.Instance);
    if (args.Contains("cikarim"))
    {
        await DraftEval.RunIntakeAsync(new Hukuk.AI.Drafting.DraftIntake(chat), Path.Combine(dataDir, "eval", "draft-requests-v1.json"), Path.Combine(dataDir, "eval", "results"));
        return;
    }
    await DraftEval.RunAsync(new Hukuk.AI.Drafting.DraftIntake(chat), writer, chat, Path.Combine(dataDir, "eval", "draft-requests-v1.json"), Path.Combine(dataDir, "eval", "results"));
    return;
}

// "dotnet run -- belgesoru": sentetik sözleşmeler üzerinde soru-cevap kalitesi (parçalar gerçek Redis'e yazılır).
if (args.Contains("belgesoru"))
{
    await using var redis = await ConnectionMultiplexer.ConnectAsync(Required("ConnectionStrings:Redis"));
    await using var docDb = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseHukukAiPostgres(Required("ConnectionStrings:DefaultConnection")).Options);
    var hybridCache = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>();
    var embedder = new QueryEmbedder(generator, hybridCache, NullLogger<QueryEmbedder>.Instance);
    var search = new KnowledgeSearch(new Retriever(docDb), embedder, NullLogger<KnowledgeSearch>.Instance);
    var chat = kernel.GetRequiredService<IChatCompletionService>();
    var store = new Hukuk.AI.Documents.DocumentStore(redis, NullLogger<Hukuk.AI.Documents.DocumentStore>.Instance);
    var docAnswers = new Hukuk.AI.Documents.DocumentAnswerService(store, embedder, generator, search, chat, NullLogger<Hukuk.AI.Documents.DocumentAnswerService>.Instance);
    var chunker = new Hukuk.AI.Documents.DocumentChunker(Microsoft.ML.Tokenizers.TiktokenTokenizer.CreateForModel("text-embedding-3-large"));
    await DocAnswerEval.RunAsync(docAnswers, store, chunker, generator, chat, Path.Combine(dataDir, "eval", "doc-questions-v1.json"),
        Path.Combine(dataDir, "eval", "contracts-v1"), Path.Combine(dataDir, "eval", "results"), matrix: args.Contains("matris"));
    return;
}

var rewriter = new RewriteCache(new QueryRewriter(kernel.GetRequiredService<IChatCompletionService>()),
    Path.Combine(dataDir, "eval", $"rewrites-v{QueryRewriter.PromptVersion}.json"));
Console.WriteLine("Sorular kanun diline yeniden yazılıyor (cache'te olmayanlar)...");
var rewrites = await rewriter.RewriteAllAsync(evalSet.Questions);

// Her soru metni bir kez embed edilir; aynı vektörler her stratejide kullanılır.
async Task<List<Vector>> EmbedAsync(IEnumerable<string> texts) =>
    (await generator.GenerateAsync(texts)).Select(e => new Vector(e.Vector)).ToList();

var original = await EmbedAsync(evalSet.Questions.Select(q => q.Question));
var rewritten = await EmbedAsync(evalSet.Questions.Select(q => rewrites[q.Id]));
var combined = await EmbedAsync(evalSet.Questions.Select(Both));
var vectors = evalSet.Questions.Select((q, i) => (q, original: original[i], rewritten: rewritten[i], combined: combined[i])).ToList();

var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseHukukAiPostgres(Required("ConnectionStrings:DefaultConnection")).Options;
await using var db = new AppDbContext(dbOptions);
var retriever = new Retriever(db);

// "dotnet run -- hnsw": HNSW'nin exact aramaya göre kaybı. Aynı soru vektörleriyle (Birlesik) iki arama yapılır;
// recall@k = HNSW'nin top-k'sında exact top-k'dan kaç chunk var. Sadece D (indeks sadece onda).
if (args.Contains("hnsw"))
{
    int[] ks = [10, fetchCount];
    Console.WriteLine($"\n== HNSW vs exact (D, {vectors.Count} soru) ==");
    Console.WriteLine($"{"ef_search",-12}" + string.Join("", ks.Select(k => $"{"recall@" + k,12}")) + $"{"indeks",10}");
    foreach (var ef in new[] { 40, 64, 100, 200 })
    {
        var sums = new double[ks.Length];
        var allUsedIndex = true;
        foreach (var x in vectors)
        {
            var exact = await retriever.SearchAsync(x.combined, ChunkStrategy.D_Hibrit, null, fetchCount);
            var (hnsw, usedIndex) = await retriever.HnswSearchAsync(x.combined, fetchCount, ef);
            allUsedIndex &= usedIndex;
            for (var i = 0; i < ks.Length; i++)
                sums[i] += (double)exact.Take(ks[i]).Count(e => hnsw.Take(ks[i]).Contains(e.Id)) / ks[i];
        }
        Console.WriteLine($"{ef,-12}" + string.Join("", sums.Select(s => $"{s / vectors.Count,12:F3}")) + $"{(allUsedIndex ? "evet" : "HAYIR"),10}");
    }
    return;
}

if (args.Contains("bench"))
{
    await ScaleBenchmark.RunAsync(Required("ConnectionStrings:DefaultConnection"), vectors.Select(x => x.combined).ToList());
    return;
}

if (args.Contains("sure"))
{
    await AnswerEval.MeasureSearchLatencyAsync(new QueryRewriter(kernel.GetRequiredService<IChatCompletionService>()), generator, evalSet.Questions);
    return;
}

if (args.Contains("cevap"))
{
    // Canlıdaki gibi yeniden yazımsız; vektörler toplu embed edildiği için embedding cache'i sadece bellekte (L1).
    var hybridCache = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>();
    var embedder = new QueryEmbedder(generator, hybridCache, NullLogger<QueryEmbedder>.Instance);
    var search = new KnowledgeSearch(retriever, embedder, NullLogger<KnowledgeSearch>.Instance);
    var answers = new LegalAnswerService(kernel.Clone(), new KnowledgeBasePlugin(search), NullLogger<LegalAnswerService>.Instance);
    await AnswerEval.RunAsync(answers, search, kernel.GetRequiredService<IChatCompletionService>(),
        vectors.Select(x => (x.q, x.original)).ToList(), Path.Combine(dataDir, "eval", "results"), liveOnly: args.Contains("canli"));
    return;
}

var scored = vectors.Where(x => x.q.IsScored).ToList();
Console.WriteLine($"\n{evalSet.Questions.Count} soru, metriklere giren {scored.Count} (belirsiz/kapsam dışı hariç)\n");

// Sorgu yöntemleri: aynı soru için aday chunk listesini farklı şekilde üretir.
// Önceki turlarda elenenler: orijinal soru, sadece yeniden yazım, vektör-vektör RRF, ts_rank_cd full-text,
// BM25/hybrid'in orijinal soruyla olanı (hepsinde Birlesik daha iyiydi), Hibrit-50/70 ve atıfta hybrid'e
// yönlendirme (eval v2'de vektörü geçemedi). BM25 ve Hibrit-80 bilgi tabanı büyüyünce yeniden ölçmek için duruyor.
string Both(EvalQuestion q) => QueryRewriter.Combine(q.Question, rewrites[q.Id]);

async Task<List<RetrievedChunk>> HybridAsync(EvalQuestion q, Vector v, ChunkStrategy s, string? law, double vectorWeight) =>
    Retriever.FuseRrf([
        (await retriever.SearchAsync(v, s, law, fetchCount), vectorWeight),
        (await retriever.Bm25SearchAsync(Both(q), s, law, fetchCount), 1 - vectorWeight)]);

var queryMethods = new (string Name, Func<(EvalQuestion q, Vector original, Vector rewritten, Vector combined), ChunkStrategy, string?, Task<List<RetrievedChunk>>> Search)[]
{
    ("Vektor", (x, s, law) => retriever.SearchAsync(x.combined, s, law, fetchCount)),
    ("BM25", (x, s, law) => retriever.Bm25SearchAsync(Both(x.q), s, law, fetchCount)),
    // Hibrit-80: vektör ağırlığı %80, BM25 ağırlığı %20
    ("Hibrit-80", (x, s, law) => HybridAsync(x.q, x.combined, s, law, 0.8)),
    // Soruda madde atfı varsa o madde doğrudan getirilip başa konur, kalan bağlam vektör aramasıyla dolar.
    ("DogrudanMadde", async (x, s, law) =>
    {
        var vector = await retriever.SearchAsync(x.combined, s, law, fetchCount);
        var references = QueryRouter.Parse(x.q.Question);
        if (references.Count == 0)
            return vector;
        return KnowledgeSearch.PrependDirect(await retriever.GetByArticlesAsync(references, s), vector);
    }),
    // Kazanan yapının yeniden yazımsız hali (2026-09-29): yeniden yazım ~5 sn, kaldırılırsa ne kaybedilir?
    ("HamDogrudanMadde", async (x, s, law) =>
    {
        var vector = await retriever.SearchAsync(x.original, s, law, fetchCount);
        var references = QueryRouter.Parse(x.q.Question);
        if (references.Count == 0)
            return vector;
        return KnowledgeSearch.PrependDirect(await retriever.GetByArticlesAsync(references, s), vector);
    }),
};

foreach (var x in scored.Where(x => QueryRouter.HasLegalReference(x.q.Question)))
    Console.WriteLine($"  Madde atfı: {x.q.Id} -> {string.Join(", ", QueryRouter.Parse(x.q.Question).Select(r => $"{r.Law ?? "?"}/{r.Type}/{r.No}"))}");
Console.WriteLine();

var details = new List<object>();
var modes = args.Contains("filtreli") ? new[] { false, true } : [false];

foreach (var filtered in modes)
{
    // Soru başına aramayı bir kez yap, bütçeler aynı sıralamadan kesilir.
    var searches = new List<(ChunkStrategy strategy, string method, List<(EvalQuestion q, List<RetrievedChunk> results)> perQuestion)>();
    foreach (var strategy in Enum.GetValues<ChunkStrategy>())
        foreach (var (method, search) in queryMethods)
        {
            var perQuestion = new List<(EvalQuestion, List<RetrievedChunk>)>();
            foreach (var x in scored)
                perQuestion.Add((x.q, await search(x, strategy, filtered ? x.q.Law : null)));
            searches.Add((strategy, method, perQuestion));
        }

    foreach (var budget in budgets)
    {
        Console.WriteLine($"== {(filtered ? "Kanun filtreli" : "Filtresiz")}, bütçe {budget} token ==");
        Console.WriteLine($"{"",-35}{"-- Tümü --",26}{"-- Recall / MRR üsluba göre --",42}");
        Console.WriteLine($"{"Strateji",-18}{"Sorgu",-17}{"Recall",8}{"Precision",11}{"MRR",7}{"Günlük",14}{"Madde atfı",14}{"Hukuki",14}");

        foreach (var (strategy, method, perQuestion) in searches)
        {
            double recall = 0, precision = 0, mrr = 0;
            var byStyle = new Dictionary<QuestionStyle, (double recall, double mrr, int n)>();

            foreach (var (q, results) in perQuestion)
            {
                var context = KnowledgeSearch.WithinBudget(results, budget);
                var ranked = context.Select(r => r.Articles).ToList();
                // Eval setindeki numaralar asıl madde numaraları.
                var expected = q.ExpectedArticles.Select(n => new ArticleRef(ArticleType.Asil, n)).ToHashSet();

                recall += Metrics.RecallAt(ranked.Count, ranked, expected);
                precision += Metrics.PrecisionAt(ranked.Count, ranked, expected);
                var rr = Metrics.ReciprocalRank(ranked.Count, ranked, expected);
                mrr += rr;
                var questionRecall = Metrics.RecallAt(ranked.Count, ranked, expected);
                var st = byStyle.GetValueOrDefault(q.Style);
                byStyle[q.Style] = (st.recall + questionRecall, st.mrr + rr, st.n + 1);

                details.Add(new
                {
                    Filtered = filtered, Budget = budget, Strategy = strategy, Method = method, q.Id, q.Type, q.Style, q.Question,
                    Rewrite = rewrites[q.Id], Expected = q.ExpectedArticles, ReciprocalRank = rr,
                    Results = context.Select(r => new { Articles = string.Join(",", r.Articles.Select(Label)), r.TokenCount, Similarity = Math.Round(r.Similarity, 4) }),
                });
            }

            var n = perQuestion.Count;
            string Style(QuestionStyle style) => byStyle.TryGetValue(style, out var v) ? $"{v.recall / v.n:F3}/{v.mrr / v.n:F3}" : "-";
            Console.WriteLine($"{strategy,-18}{method,-17}{recall / n,8:F3}{precision / n,11:F3}{mrr / n,7:F3}" +
                              $"{Style(QuestionStyle.Gunluk),14}{Style(QuestionStyle.MaddeRef),14}{Style(QuestionStyle.Hukuki),14}");
        }
        Console.WriteLine();
    }
}

// Kapsam dışı tespiti için ön bilgi: en iyi sonucun benzerliği kapsamdaki sorularla ayrışıyor mu? (filtresiz)
Console.WriteLine("== En iyi sonucun ortalama benzerliği (filtresiz, orijinal soru) ==");
foreach (var strategy in Enum.GetValues<ChunkStrategy>())
{
    var byType = new Dictionary<QuestionType, List<double>>();
    foreach (var (q, v, _, _) in vectors)
    {
        var top = (await retriever.SearchAsync(v, strategy, null, 1))[0];
        byType.TryAdd(q.Type, []);
        byType[q.Type].Add(top.Similarity);
    }
    Console.WriteLine($"{strategy,-18}" + string.Join("  ", byType.Select(kv => $"{kv.Key}: {kv.Value.Average():F3}")));
}

var resultsDir = Path.Combine(dataDir, "eval", "results");
Directory.CreateDirectory(resultsDir);
var outPath = Path.Combine(resultsDir, $"retrieval-{DateTime.Now:yyyyMMdd-HHmmss}.json");
File.WriteAllText(outPath, JsonSerializer.Serialize(details, new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    Converters = { new JsonStringEnumConverter() },
}));
Console.WriteLine($"\nSoru bazlı detaylar -> {outPath}");

static string Label(ArticleRef r) => r.Type switch
{
    ArticleType.Ek => $"Ek{r.No}",
    ArticleType.Gecici => $"G{r.No}",
    _ => r.No.ToString(),
};

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hukuk.AI.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("Hukuk.AI.slnx bulunamadı.");
}
