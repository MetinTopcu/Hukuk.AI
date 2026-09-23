using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Hukuk.AI.Data.Entities;

namespace Hukuk.AI.Ingestion.Parsing;

// mevzuat.gov.tr'den indirilen kanun HTML'ini maddelere ayırır.
//
// Başlıklar bazen <h2>, bazen <p> olarak geliyor; bu yüzden etikete değil konuma bakılır:
// "MADDE N-" satırından hemen önce gelen kısa satır zinciri başlıktır, gerisi madde metnidir.
public static partial class MevzuatParser
{
    // "MADDE 299-", "Madde 33 –", "Madde 87 (Mülga...", "Geçici Madde 1 -", "EK MADDE 2-"
    [GeneratedRegex(@"^(?:(Ek|EK|Geçici|GEÇİCİ)\s+)?(?:MADDE|Madde)\s+(\d+)\s*[-–—]?\s*(.*)$")]
    private static partial Regex ArticleStartRegex();

    // "DÖRDÜNCÜ BÖLÜM", "İKİNCİ AYIRIM"
    [GeneratedRegex(@"^(?:[A-ZÇĞİÖŞÜ]+\s+)?(KİTAP|KISIM|BÖLÜM|AYIRIM)$")]
    private static partial Regex StructuralRegex();

    // Kenar başlığı önekleri: "A.", "IV.", "2.", "b.", "aa."
    [GeneratedRegex(@"^([IVX]+|[A-ZÇĞİÖŞÜ]|\d+|[a-zçğıöşü]{1,2})\.\s+\S")]
    private static partial Regex MarginPrefixRegex();

    // Bent işaretleri: "a) ...", "1) ..." -> başlık değil, metin
    [GeneratedRegex(@"^([a-zçğıöşü]{1,2}|\d+)\)")]
    private static partial Regex ListItemRegex();

    // Temizlenemeyen dipnot kalıntıları: "[12]"
    [GeneratedRegex(@"\[\d+\]")]
    private static partial Regex FootnoteMarkRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // Maddenin başındaki "(Mülga: 20/6/2012-6331/37 md.)" gibi değişiklik notu
    [GeneratedRegex(@"^\((?:[^()]|\([^()]*\))*\)\s*")]
    private static partial Regex LeadingNoteRegex();

    private static readonly string[] StructuralOrder = ["KİTAP", "KISIM", "BÖLÜM", "AYIRIM"];

    // Önek tipine göre kenar başlığı seviyesi; öneksiz başlıklar (İş Kanunu) en alt seviye sayılır.
    private const int UnprefixedLevel = 9;

    public static List<ParsedArticle> Parse(string law, string html)
    {
        // Dosyadaki <meta charset=Windows-1254> yanlış; string olarak verince AngleSharp onu dikkate almaz.
        var document = new HtmlParser().ParseDocument(html);

        // Dipnot referansları, dipnot metinleri ve değişiklik tabloları madde metnine karışmasın.
        foreach (var junk in document.QuerySelectorAll(
                     ".MsoFootnoteReference, a[href^='#_ftn'], sup, table, .MsoFootnoteText, div[id^='ftn']").ToList())
            junk.Remove();

        var blocks = document.QuerySelectorAll("h1, h2, h3, h4, h5, h6, p")
            .Select(e => Normalize(e.TextContent))
            .Where(t => t.Length > 0)
            .ToList();

        var structural = new SortedDictionary<int, string>();   // seviye -> "Kira Sözleşmesi"
        var margin = new List<(int Level, string Text)>();       // kenar başlıkları yığını
        var pending = new List<string>();                        // sıradaki maddenin başlıkları
        var articles = new List<ParsedArticle>();

        ArticleType type = default;
        int articleNo = 0;
        string sectionPath = "", heading = "";
        List<string>? body = null;

        void Flush()
        {
            if (body is null) return;
            var content = string.Join("\n", body);
            articles.Add(new ParsedArticle(law, type, articleNo, sectionPath, heading, content, HasNoText(content)));
            body = null;
        }

        for (var i = 0; i < blocks.Count; i++)
        {
            var text = blocks[i];

            // Kanun metninden sonra gelen "... SAYILI KANUNA EK VE DEĞİŞİKLİK GETİREN MEVZUAT..." listesi madde değil.
            if (text.Contains("KANUNA EK VE DEĞİŞİKLİK GETİREN")) break;

            var start = ArticleStartRegex().Match(text);

            if (start.Success)
            {
                Flush();
                // Öneksiz kenar başlığı (İş Kanunu) tek bir maddeye aittir; başlıksız madde onu devralmasın.
                margin.RemoveAll(m => m.Level == UnprefixedLevel);
                ApplyHeadings(pending, structural, margin);
                pending.Clear();

                type = ToArticleType(start.Groups[1].Value);
                articleNo = int.Parse(start.Groups[2].Value);
                heading = margin.Count > 0 ? margin[^1].Text : "";
                sectionPath = string.Join(" > ", structural.Values.Concat(margin.Select(m => m.Text)));
                body = [];
                if (start.Groups[3].Value.Length > 0) body.Add(start.Groups[3].Value);
            }
            else if (IsHeadingLike(text) && IsFollowedByArticle(blocks, i))
            {
                pending.Add(text);
            }
            else
            {
                body?.Add(text); // ilk maddeden önceki künye satırları atılır
            }
        }

        Flush();
        return articles;
    }

    private static void ApplyHeadings(List<string> headings, SortedDictionary<int, string> structural,
        List<(int Level, string Text)> margin)
    {
        for (var i = 0; i < headings.Count; i++)
        {
            var h = headings[i];
            var s = StructuralRegex().Match(h);
            if (s.Success)
            {
                var level = Array.IndexOf(StructuralOrder, s.Groups[1].Value);
                foreach (var k in structural.Keys.Where(k => k >= level).ToList()) structural.Remove(k);
                margin.Clear();

                // "DÖRDÜNCÜ BÖLÜM" satırından sonraki satır bölümün adıdır ("Kira Sözleşmesi").
                var hasTitle = i + 1 < headings.Count && !StructuralRegex().IsMatch(headings[i + 1])
                                                      && !MarginPrefixRegex().IsMatch(headings[i + 1]);
                structural[level] = hasTitle ? headings[++i] : h;
                continue;
            }

            var marginLevel = MarginLevel(h);
            margin.RemoveAll(m => m.Level >= marginLevel);
            margin.Add((marginLevel, h));
        }
    }

    private static int MarginLevel(string heading)
    {
        var m = MarginPrefixRegex().Match(heading);
        if (!m.Success) return UnprefixedLevel;

        var p = m.Groups[1].Value;
        // Not: "I." hem Roma rakamı hem H'den sonraki harf olabilir; kira maddelerinde hep Roma rakamı.
        if (p.All(c => c is 'I' or 'V' or 'X')) return 2;
        if (char.IsUpper(p[0])) return 1;
        if (char.IsDigit(p[0])) return 3;
        return p.Length == 1 ? 4 : 5;
    }

    // Kısa, noktalama ile bitmeyen, bent/not olmayan satır
    private static bool IsHeadingLike(string text) =>
        text.Length <= 150
        && !text.StartsWith('(')
        && !ListItemRegex().IsMatch(text)
        && !".:;,".Contains(text[^1]);

    // Başlık zinciri bir "MADDE N-" satırıyla bitiyorsa bu satır başlıktır.
    private static bool IsFollowedByArticle(List<string> blocks, int index)
    {
        for (var j = index + 1; j < blocks.Count; j++)
        {
            if (ArticleStartRegex().IsMatch(blocks[j])) return true;
            if (!IsHeadingLike(blocks[j])) return false;
        }
        return false;
    }

    private static bool HasNoText(string content) =>
        LeadingNoteRegex().Replace(content, "").Trim().Length == 0;

    private static ArticleType ToArticleType(string prefix) => prefix switch
    {
        "Ek" or "EK" => ArticleType.Ek,
        "Geçici" or "GEÇİCİ" => ArticleType.Gecici,
        _ => ArticleType.Asil
    };

    private static string Normalize(string text) =>
        WhitespaceRegex().Replace(FootnoteMarkRegex().Replace(text, ""), " ").Trim();
}
