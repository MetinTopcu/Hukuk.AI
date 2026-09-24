using Hukuk.AI.Data.Entities;
using Hukuk.AI.Ingestion.Parsing;
using Microsoft.ML.Tokenizers;

namespace Hukuk.AI.Ingestion.Chunking;

// Strateji D: C gibi madde + başlık; madde maxTokens'ı aşarsa ardışık fıkralar maxTokens'a kadar
// gruplanır ve her grup aynı başlığı taşır. Tek fıkra sınırı aşarsa bölünmeden kalır.
public class HybridArticleChunker(Tokenizer tokenizer, IReadOnlyDictionary<string, string> lawNames, int maxTokens = 800) : IChunker
{
    public string Name => "D";

    public IEnumerable<Chunk> Split(string law, IReadOnlyList<ParsedArticle> articles)
    {
        foreach (var a in articles)
        {
            var header = ContextualArticleChunker.Header(lawNames[law], a);
            ArticleRef[] refs = [new ArticleRef(a.ArticleType, a.ArticleNo)];

            var whole = header + a.Content;
            var wholeTokens = tokenizer.CountTokens(whole);
            if (wholeTokens <= maxTokens)
            {
                yield return new Chunk(Name, law, refs, whole, wholeTokens);
                continue;
            }

            var group = new List<string>();
            foreach (var paragraph in a.Content.Split('\n'))
            {
                var candidate = header + string.Join("\n", group.Append(paragraph));
                if (group.Count > 0 && tokenizer.CountTokens(candidate) > maxTokens)
                {
                    yield return Build(law, refs, header, group);
                    group.Clear();
                }
                group.Add(paragraph);
            }
            yield return Build(law, refs, header, group);
        }
    }

    private Chunk Build(string law, ArticleRef[] refs, string header, List<string> group)
    {
        var text = header + string.Join("\n", group);
        return new Chunk(Name, law, refs, text, tokenizer.CountTokens(text));
    }
}
