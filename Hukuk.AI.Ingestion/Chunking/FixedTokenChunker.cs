using Hukuk.AI.Data.Entities;
using Hukuk.AI.Ingestion.Parsing;
using Microsoft.ML.Tokenizers;

namespace Hukuk.AI.Ingestion.Chunking;

// Strateji B: tüm maddeler tek token dizisi, sabit boyutlu kayan pencere (madde sınırlarını aşar).
public class FixedTokenChunker(Tokenizer tokenizer, int size = 400, int overlap = 50) : IChunker
{
    public string Name => "B";

    public IEnumerable<Chunk> Split(string law, IReadOnlyList<ParsedArticle> articles)
    {
        // Paralel listeler: her token'ın hangi maddeye ait olduğu tutulur.
        var ids = new List<int>();
        var owners = new List<ArticleRef>();

        foreach (var a in articles)
        {
            var t = tokenizer.EncodeToIds(a.Content + "\n\n");
            ids.AddRange(t);
            owners.AddRange(Enumerable.Repeat(new ArticleRef(a.ArticleType, a.ArticleNo), t.Count));
        }

        var step = size - overlap;
        for (var start = 0; start < ids.Count; start += step)
        {
            var end = Math.Min(start + size, ids.Count);
            var window = ids.GetRange(start, end - start);
            var refs = owners.GetRange(start, end - start).Distinct().ToArray();

            // Token sınırında kesmek Türkçe harfleri bölebilir; kenarlarda "�" görülebilir (baseline için kabul).
            yield return new Chunk(Name, law, refs, tokenizer.Decode(window), window.Count);

            if (end == ids.Count)
                yield break;
        }
    }
}
