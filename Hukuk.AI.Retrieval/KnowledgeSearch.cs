using System.Diagnostics;
using Hukuk.AI.Data.Entities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace Hukuk.AI.Retrieval;

// LLM'e bağlam olarak verilecek kaynak. Number cevaptaki atıf numarası ([1], [2] ...).
public record KnowledgeSource(int Number, string Label, ArticleRef[] Articles, string LawName, string Heading, string Content, int TokenCount, double Similarity);

// Eval'de kazanan yapı (2026-09-24): strateji D + Birlesik sorgu + madde atfında doğrudan getirme + exact vektör,
// kanun filtresi yok, BM25 yok. Bütçe 4000 (D@4000 R 1.000 MRR .868; @2000 R .970); cevap eval'inde de 4000 hem
// puanda hem atıfta 2000'den iyi, süre farkı yok (2026-09-27).
public class KnowledgeSearch(Retriever retriever, QueryRewriter rewriter, IEmbeddingGenerator<string, Embedding<float>> generator, ILogger<KnowledgeSearch> logger)
{
    public const ChunkStrategy Strategy = ChunkStrategy.D_Hibrit;
    public const int FetchCount = 50; // en büyük bütçeyi doldurmaya yetecek kadar aday chunk
    public const int TokenBudget = 4000;

    public async Task<List<KnowledgeSource>> SearchAsync(string question, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var rewrite = await rewriter.RewriteAsync(question, cancellationToken);
        var rewriteMs = sw.ElapsedMilliseconds;

        var embedding = await generator.GenerateVectorAsync(QueryRewriter.Combine(question, rewrite), cancellationToken: cancellationToken);
        var embedMs = sw.ElapsedMilliseconds - rewriteMs;

        var sources = await SearchAsync(question, new Vector(embedding), TokenBudget);
        logger.LogInformation("Arama: yeniden yazım {RewriteMs} ms, embedding {EmbedMs} ms, veritabanı {DbMs} ms, {Count} kaynak",
            rewriteMs, embedMs, sw.ElapsedMilliseconds - rewriteMs - embedMs, sources.Count);
        return sources;
    }

    // Sorgu vektörü hazırsa (eval: yeniden yazımlar cache'ten, vektörler toplu embed edilir).
    public async Task<List<KnowledgeSource>> SearchAsync(string question, Vector queryVector, int tokenBudget)
    {
        var vector = await retriever.SearchAsync(queryVector, Strategy, null, FetchCount);
        var direct = await retriever.GetByArticlesAsync(QueryRouter.Parse(question), Strategy);
        var context = WithinBudget(PrependDirect(direct, vector), tokenBudget);

        var texts = await retriever.GetTextsAsync(context.Select(c => c.Id).ToList());
        return texts.Select((t, i) => new KnowledgeSource(i + 1, Label(t.Law, t.Articles), t.Articles, t.LawName, t.Heading, t.Content, context[i].TokenCount, context[i].Similarity))
            .ToList();
    }

    // Soruda madde atfı varsa o madde doğrudan getirilip başa konur, kalan bağlam vektör aramasıyla dolar.
    public static List<RetrievedChunk> PrependDirect(List<RetrievedChunk> direct, List<RetrievedChunk> vector) =>
        [.. direct, .. vector.Where(v => direct.All(d => d.Id != v.Id))];

    // Sırayla ekle, sığmayan ilk chunk'ta dur. İlk chunk bütçeden büyük olsa bile alınır (yoksa bağlam boş kalır).
    public static List<RetrievedChunk> WithinBudget(List<RetrievedChunk> results, int budget) =>
        WithinBudget(results, r => r.TokenCount, budget);

    public static List<KnowledgeSource> WithinBudget(List<KnowledgeSource> sources, int budget) =>
        WithinBudget(sources, s => s.TokenCount, budget);

    private static List<T> WithinBudget<T>(List<T> items, Func<T, int> tokens, int budget)
    {
        var context = new List<T>();
        var used = 0;
        foreach (var item in items)
        {
            if (context.Count > 0 && used + tokens(item) > budget)
                break;
            context.Add(item);
            used += tokens(item);
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
