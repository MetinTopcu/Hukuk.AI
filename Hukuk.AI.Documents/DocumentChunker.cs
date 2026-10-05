using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace Hukuk.AI.Documents;

// Label: atıf etiketi ("md. 7", yoksa başlık). Content: embedding'e giden metin (dosya adı + başlık + gövde).
public record DocumentChunk(int Index, string Label, string? Heading, int Page, string Content, int TokenCount);

// Yüklenen belgenin parçalanması (karar 2026-09-30): bilgi tabanındaki kazanan D stratejisinin karşılığı.
// Bölüm sınırı = Markdown başlığı (# ...) veya "MADDE 7" ile başlayan paragraf. Bölüm maxTokens'a sığarsa tek parça,
// sığmazsa ardışık paragraflar maxTokens'a kadar gruplanır; her parça bölüm başlığını taşır (bağlam + atıf).
// Tek paragraf sınırı aşarsa (OCR bazen paragraf ayırmaz) token sınırında kelime arasından bölünür.
// İlk başlıktan önceki metin (sözleşmelerde genelde taraflar) "Başlangıç" bölümüdür.
public partial class DocumentChunker(Tokenizer tokenizer, int maxTokens = 500)
{
    private record Block(string Text, int Page);

    private record Section(string? Heading, string? ArticleNo, List<Block> Blocks);

    public List<DocumentChunk> Split(string fileName, string markdown)
    {
        var chunks = new List<DocumentChunk>();
        foreach (var section in Sections(Blocks(markdown)))
        {
            var header = section.Heading is null ? $"{fileName}\n" : $"{fileName}\n{section.Heading}\n";
            var label = section.ArticleNo is { } no ? $"md. {no}" : section.Heading is { } h ? Shorten(h) : "Başlangıç";
            var budget = Math.Max(50, maxTokens - tokenizer.CountTokens(header));

            var group = new List<Block>();
            var groupTokens = 0;
            foreach (var block in section.Blocks.SelectMany(b => SplitLong(b, budget)))
            {
                var tokens = tokenizer.CountTokens(block.Text);
                if (group.Count > 0 && groupTokens + tokens > budget)
                {
                    chunks.Add(Build(chunks.Count, label, section.Heading, header, group));
                    group.Clear();
                    groupTokens = 0;
                }
                group.Add(block);
                groupTokens += tokens;
            }
            if (group.Count > 0)
                chunks.Add(Build(chunks.Count, label, section.Heading, header, group));
        }
        return chunks;
    }

    private DocumentChunk Build(int index, string label, string? heading, string header, List<Block> group)
    {
        var text = header + string.Join("\n\n", group.Select(b => b.Text));
        return new DocumentChunk(index, label, heading, group[0].Page, text, tokenizer.CountTokens(text));
    }

    // Boş satırla ayrılmış paragraflar + sayfa numarası. Document Intelligence'ın sayfa işaretleri (PageBreak) sayfayı
    // ilerletir; sayfa üst/alt bilgisi ve sayfa numarası yorumları atılır (her sayfada tekrar eden gürültü).
    private static IEnumerable<Block> Blocks(string markdown)
    {
        var page = 1;
        var current = new StringBuilder();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("<!-- PageBreak -->"))
            {
                if (current.Length > 0) { yield return new Block(current.ToString(), page); current.Clear(); }
                page++;
                continue;
            }
            if (PageNoise().IsMatch(line))
                continue;
            if (line.Length == 0)
            {
                if (current.Length > 0) { yield return new Block(current.ToString(), page); current.Clear(); }
                continue;
            }
            if (current.Length > 0)
                current.Append('\n');
            current.Append(line);
        }
        if (current.Length > 0)
            yield return new Block(current.ToString(), page);
    }

    private static IEnumerable<Section> Sections(IEnumerable<Block> blocks)
    {
        var section = new Section(null, null, []);
        foreach (var block in blocks)
        {
            string? heading = null;
            Block? body = block;
            if (block.Text.StartsWith('#'))
            {
                // Başlık bloğu: ilk satır başlık, varsa devamı gövde.
                var lines = block.Text.Split('\n', 2);
                heading = lines[0].TrimStart('#').Trim();
                body = lines.Length > 1 ? block with { Text = lines[1] } : null;
            }
            else if (Article().IsMatch(block.Text))
            {
                // Madde satırı her parçanın başlığına zaten giriyor; kısaysa gövdeden çıkar (tekrar token harcamasın).
                var lines = block.Text.Split('\n', 2);
                var first = lines[0].Replace("*", "").Trim();
                heading = Shorten(first, 120);
                if (heading == first)
                    body = lines.Length > 1 ? block with { Text = lines[1] } : null;
            }

            if (heading is not null)
            {
                if (section.Blocks.Count > 0)
                    yield return section;
                var no = Article().Match(heading) is { Success: true } m ? m.Groups[1].Value
                    : Article().Match(block.Text) is { Success: true } mb ? mb.Groups[1].Value : null;
                section = new Section(heading, no, []);
            }
            if (body is not null)
                section.Blocks.Add(body);
        }
        if (section.Blocks.Count > 0)
            yield return section;
    }

    private IEnumerable<Block> SplitLong(Block block, int budget)
    {
        var text = block.Text;
        while (tokenizer.CountTokens(text) > budget)
        {
            var end = tokenizer.GetIndexByTokenCount(text, budget, out _, out _);
            // Tercih sırası: cümle sonu, kelime arası; sınıra çok uzaksa (parçanın ilk yarısı) olduğu yerden.
            var sentence = text.LastIndexOf(". ", Math.Max(0, end - 1), StringComparison.Ordinal);
            var space = text.LastIndexOfAny([' ', '\n'], Math.Max(0, end - 1));
            if (sentence > end / 2)
                end = sentence + 1;
            else if (space > end / 2)
                end = space;
            end = Math.Max(end, 1); // her turda ilerle
            yield return block with { Text = text[..end].Trim() };
            text = text[end..].Trim();
        }
        if (text.Length > 0)
            yield return block with { Text = text };
    }

    private static string Shorten(string text, int max = 60) => text.Length <= max ? text : text[..max].TrimEnd() + "…";

    // "MADDE 7", "Madde 7 -", "**MADDE 7:**" (satır başında). Grup 1: madde numarası.
    [GeneratedRegex(@"^\**\s*madde\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Article();

    [GeneratedRegex(@"^<!-- Page(Header|Footer|Number)=")]
    private static partial Regex PageNoise();
}
