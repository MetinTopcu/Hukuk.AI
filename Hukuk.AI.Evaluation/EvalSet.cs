using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hukuk.AI.Evaluation;

// data/eval/eval-set-v{N}.json
public record EvalSet(int Version, List<EvalQuestion> Questions)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static EvalSet Load(string path) =>
        JsonSerializer.Deserialize<EvalSet>(File.ReadAllText(path), Options)
        ?? throw new InvalidOperationException($"Eval seti okunamadı: {path}");
}

public record EvalQuestion(
    string Id,
    // Belirsiz ve kapsam dışı sorularda null
    string? Law,
    QuestionType Type,
    // Asıl madde numaraları; ilki birincil. Belirsiz ve kapsam dışı sorularda boş.
    int[] ExpectedArticles,
    string Question,
    string AnswerKey,
    // v2'den itibaren; v1 sorularında "gunluk"
    QuestionStyle Style = QuestionStyle.Gunluk)
{
    // Beklenen maddesi olan sorular metriklere girer (tek_madde + cok_madde).
    public bool IsScored => ExpectedArticles.Length > 0;
}

public enum QuestionType
{
    TekMadde,
    CokMadde,
    Belirsiz,
    KapsamDisi,
}

public enum QuestionStyle
{
    Gunluk,    // günlük dil ("ev sahibim depozitomu...")
    MaddeRef,  // kanun/madde atfı var ("TBK 344 uyarınca...")
    Hukuki,    // hukuk terminolojisi, madde atfı yok
}
