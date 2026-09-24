using Hukuk.AI.Data.Entities;
using Hukuk.AI.Ingestion.Parsing;
using Microsoft.ML.Tokenizers;

namespace Hukuk.AI.Ingestion.Chunking;

// Strateji C: her madde tek chunk, başına kanun + madde + bölüm yolu başlığı eklenir.
public class ContextualArticleChunker(Tokenizer tokenizer, IReadOnlyDictionary<string, string> lawNames) : IChunker
{
    public string Name => "C";

    public IEnumerable<Chunk> Split(string law, IReadOnlyList<ParsedArticle> articles)
    {
        foreach (var a in articles)
        {
            var text = Header(lawNames[law], a) + a.Content;
            yield return new Chunk(Name, law, [new ArticleRef(a.ArticleType, a.ArticleNo)], text, tokenizer.CountTokens(text));
        }
    }

    // Strateji D de aynı başlığı kullanır. SectionPath kenar başlığını zaten içeriyor, Heading ayrıca eklenmiyor.
    internal static string Header(string lawName, ParsedArticle a) =>
        $"{lawName} ({a.Law}) {ArticleLabel(a)}\n{a.SectionPath}\n\n";

    private static string ArticleLabel(ParsedArticle a) => a.ArticleType switch
    {
        ArticleType.Ek => $"Ek Madde {a.ArticleNo}",
        ArticleType.Gecici => $"Geçici Madde {a.ArticleNo}",
        _ => $"Madde {a.ArticleNo}",
    };
}
