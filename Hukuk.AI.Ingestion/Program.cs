using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hukuk.AI.Data.Entities;
using Hukuk.AI.Ingestion.Parsing;

// Adım 1: data/raw/*.html -> data/parsed/*.json (bilgi tabanına girecek maddeler)

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

foreach (var law in laws)
{
    var html = File.ReadAllText(Path.Combine(dataDir, "raw", $"{law.Law}.html"));
    var all = MevzuatParser.Parse(law.Law, html);

    var duplicates = all.GroupBy(a => (a.ArticleType, a.ArticleNo)).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
    var inScope = all.Where(law.Filter).ToList();
    var kept = inScope.Where(a => !a.HasNoText).ToList();

    var output = new { law.Law, law.LawName, Count = kept.Count, Articles = kept };
    var outPath = Path.Combine(dataDir, "parsed", $"{law.Law}.json");
    File.WriteAllText(outPath, JsonSerializer.Serialize(output, jsonOptions));

    Console.WriteLine($"{law.Law} {law.LawName}: toplam {all.Count} madde, kapsamda {inScope.Count}, " +
                      $"metinsiz (mülga/işlenmiş) {inScope.Count - kept.Count}, yazılan {kept.Count} -> {outPath}");
    foreach (var d in duplicates)
        Console.WriteLine($"  UYARI: tekrar eden madde {d.ArticleType} {d.ArticleNo}");
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hukuk.AI.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("Hukuk.AI.slnx bulunamadı.");
}

record LawSource(string Law, string LawName, Func<ParsedArticle, bool> Filter);
