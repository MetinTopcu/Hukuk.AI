using Hukuk.AI.Data.Entities;

namespace Hukuk.AI.Evaluation;

// Chunk seviyesinde metrikler: LLM'e giden şey chunk olduğu için k = getirilen chunk sayısı.
// Bir chunk, beklenen maddelerden en az birini içeriyorsa alakalıdır.
public static class Metrics
{
    // Beklenen maddelerin kaçı ilk k chunk'ta geçiyor? (Aynı madde birden çok chunk'ta gelse de bir kez sayılır.)
    public static double RecallAt(int k, IReadOnlyList<ArticleRef[]> ranked, IReadOnlySet<ArticleRef> expected)
    {
        var found = ranked.Take(k).SelectMany(a => a).Where(expected.Contains).ToHashSet();
        return (double)found.Count / expected.Count;
    }

    // İlk k chunk'ın kaçı alakalı?
    public static double PrecisionAt(int k, IReadOnlyList<ArticleRef[]> ranked, IReadOnlySet<ArticleRef> expected) =>
        (double)ranked.Take(k).Count(a => IsRelevant(a, expected)) / k;

    // İlk alakalı chunk'ın sırasının tersi (1., 1/2, 1/3 ...); ilk maxK içinde yoksa 0.
    public static double ReciprocalRank(int maxK, IReadOnlyList<ArticleRef[]> ranked, IReadOnlySet<ArticleRef> expected)
    {
        var index = ranked.Take(maxK).ToList().FindIndex(a => IsRelevant(a, expected));
        return index < 0 ? 0 : 1.0 / (index + 1);
    }

    private static bool IsRelevant(ArticleRef[] chunkArticles, IReadOnlySet<ArticleRef> expected) =>
        chunkArticles.Any(expected.Contains);
}
