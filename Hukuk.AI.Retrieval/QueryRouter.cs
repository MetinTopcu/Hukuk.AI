using System.Text.RegularExpressions;
using Hukuk.AI.Data.Entities;

namespace Hukuk.AI.Retrieval;

// Soruda açık madde atfı var mı ("TBK 344", "İş K. m. 17")? Varsa o madde aranmadan doğrudan metadata ile getirilir.
// Ölçüm: vektör araması da BM25 de "TBK 347 ne düzenliyor?" sorusunda md. 347'yi bulamadı; numarayı anlamsal arama çözemez.
public static partial class QueryRouter
{
    public record LegalReference(string? Law, ArticleType Type, int No);

    // Kanun kısaltması/numarası -> bilgi tabanındaki kanun kodu
    private static readonly (Regex Pattern, string Law)[] Laws =
    [
        (new Regex(@"\b(TBK|6098)\b|Borçlar Kanunu", RegexOptions.IgnoreCase), "6098"),
        (new Regex(@"\b4857\b|\bİş\s*(K\.|Kanunu)", RegexOptions.IgnoreCase), "4857"),
    ];

    public static List<LegalReference> Parse(string question)
    {
        // Birden fazla kanun anılıyorsa hangi maddenin hangisine ait olduğu belirsiz; kanunsuz arama yapılır.
        var laws = Laws.Where(l => l.Pattern.IsMatch(question)).Select(l => l.Law).Distinct().ToList();
        var law = laws.Count == 1 ? laws[0] : null;

        return ArticleReference().Matches(question)
            .Select(m =>
            {
                var type = m.Groups["kind"].Value.ToLowerInvariant() switch
                {
                    "ek" => ArticleType.Ek,
                    "geçici" or "gecici" => ArticleType.Gecici,
                    _ => ArticleType.Asil,
                };
                var no = int.Parse(m.Groups.Values.Skip(1).First(g => g.Success && g.Name.StartsWith("no")).Value);
                return new LegalReference(law, type, no);
            })
            .Distinct()
            .ToList();
    }

    public static bool HasLegalReference(string question) => Parse(question).Count > 0;

    // "ek madde 3", "geçici madde 1", "m. 315", "md. 312", "madde 17", "352. madde(si)", "TBK 344", "İş K. 41", "İş Kanunu 32"
    // Kanun numarası ("4857 sayılı") madde sayılmaz.
    [GeneratedRegex(
        @"\b(?<kind>ek|geçici|gecici)\s+madde\s+(?<no1>\d+)" +
        @"|\b(m|md|mad)\.\s*(?<no2>\d+)" +
        @"|\bmadde\s+(?<no3>\d+)" +
        @"|\b(?<no4>\d+)\s*\.?\s*madde" +
        @"|\b(TBK|İş\s*K\.|İş\s*Kanunu)\s+(?<no5>\d{1,3})\b(?!\s*(sayılı|s\.))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArticleReference();
}
