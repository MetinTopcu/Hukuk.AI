using Hukuk.AI.Data.Entities;
using Microsoft.Extensions.AI;
using Pgvector;

namespace Hukuk.AI.Retrieval;

// LLM'e bağlam olarak verilecek kaynak. Number cevaptaki atıf numarası ([1], [2] ...).
public record KnowledgeSource(int Number, string Label, string LawName, string Heading, string Content, double Similarity);

// Eval'de kazanan yapı (2026-09-24): strateji D + Birlesik sorgu + madde atfında doğrudan getirme + exact vektör,
// kanun filtresi yok, BM25 yok. Bütçe 4000 geçici (D@4000 R 1.000 MRR .868; @2000 R .970) — cevap kalitesi ölçümü karar verecek.
public class KnowledgeSearch(Retriever retriever, QueryRewriter rewriter, IEmbeddingGenerator<string, Embedding<float>> generator)
{
    public const ChunkStrategy Strategy = ChunkStrategy.D_Hibrit;
    public const int FetchCount = 50; // en büyük bütçeyi doldurmaya yetecek kadar aday chunk
    public const int TokenBudget = 4000;

    public async Task<List<KnowledgeSource>> SearchAsync(string question, CancellationToken cancellationToken = default)
    {
        var rewrite = await rewriter.RewriteAsync(question, cancellationToken);
        var embedding = await generator.GenerateVectorAsync(QueryRewriter.Combine(question, rewrite), cancellationToken: cancellationToken);

        var vector = await retriever.SearchAsync(new Vector(embedding), Strategy, null, FetchCount);
        var direct = await retriever.GetByArticlesAsync(QueryRouter.Parse(question), Strategy);
        var context = WithinBudget(PrependDirect(direct, vector), TokenBudget);

        var texts = await retriever.GetTextsAsync(context.Select(c => c.Id).ToList());
        return texts.Select((t, i) => new KnowledgeSource(i + 1, Label(t.Law, t.Articles), t.LawName, t.Heading, t.Content, context[i].Similarity))
            .ToList();
    }

    // Soruda madde atfı varsa o madde doğrudan getirilip başa konur, kalan bağlam vektör aramasıyla dolar.
    public static List<RetrievedChunk> PrependDirect(List<RetrievedChunk> direct, List<RetrievedChunk> vector) =>
        [.. direct, .. vector.Where(v => direct.All(d => d.Id != v.Id))];

    // Sırayla ekle, sığmayan ilk chunk'ta dur. İlk chunk bütçeden büyük olsa bile alınır (yoksa bağlam boş kalır).
    public static List<RetrievedChunk> WithinBudget(List<RetrievedChunk> results, int budget)
    {
        var context = new List<RetrievedChunk>();
        var used = 0;
        foreach (var r in results)
        {
            if (context.Count > 0 && used + r.TokenCount > budget)
                break;
            context.Add(r);
            used += r.TokenCount;
        }
        return context;
    }

    // Atıf etiketi, örn: "TBK md. 344", "İş K. ek md. 2"
    private static readonly Dictionary<string, string> LawShortNames = new() { ["6098"] = "TBK", ["4857"] = "İş K." };

    public static string Label(string law, IEnumerable<ArticleRef> articles) =>
        $"{LawShortNames.GetValueOrDefault(law, law)} " + string.Join(", ", articles.Select(a => a.Type switch
        {
            ArticleType.Ek => $"ek md. {a.No}",
            ArticleType.Gecici => $"geçici md. {a.No}",
            _ => $"md. {a.No}",
        }));
}
