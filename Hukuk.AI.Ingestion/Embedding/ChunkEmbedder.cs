using System.ClientModel;
using Hukuk.AI.Data;
using Hukuk.AI.Data.Entities;
using Hukuk.AI.Ingestion.Chunking;
using Hukuk.AI.Ingestion.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector;

namespace Hukuk.AI.Ingestion.Embedding;

// Adım 3+4: bir stratejinin chunk'larını embed eder ve knowledge_chunks'a yazar.
// Idempotent: önce tüm vektörler alınır, sonra tek transaction'da o stratejinin eski satırları silinip yenileri eklenir.
public class ChunkEmbedder(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    AppDbContext db,
    IReadOnlyDictionary<string, string> lawNames,
    IReadOnlyDictionary<string, List<ParsedArticle>> articlesByLaw,
    int batchSize = 64)
{
    public async Task<int> ReplaceAsync(ChunkStrategy strategy, IReadOnlyList<Chunk> chunks)
    {
        var vectors = new List<ReadOnlyMemory<float>>(chunks.Count);
        foreach (var batch in chunks.Chunk(batchSize))
        {
            var result = await GenerateWithRetryAsync(batch.Select(c => c.Text).ToList());
            vectors.AddRange(result.Select(e => e.Vector));
            Console.WriteLine($"  {strategy}: {vectors.Count}/{chunks.Count} embed edildi");
        }

        // ChunkIndex: aynı kanun + ilk maddeyle başlayan chunk'lar içindeki sıra (D'de madde parçaları, B'de o maddede başlayan pencereler).
        var indexCounter = new Dictionary<(string, ArticleRef), int>();

        var entities = chunks.Select((c, i) =>
        {
            var key = (c.Law, c.Articles[0]);
            var index = indexCounter.GetValueOrDefault(key);
            indexCounter[key] = index + 1;

            // Tek maddelik chunk'ta başlık bilgisi maddeden gelir; B'nin çok maddeli penceresinde boş kalır.
            var article = c.Articles.Length == 1 ? Find(c.Law, c.Articles[0]) : null;

            return new KnowledgeChunk
            {
                Law = c.Law,
                LawName = lawNames[c.Law],
                // Kopya şart: EF owned (jsonb) nesneleri tek sahibe bağlar; B/D'de chunk'lar aynı ArticleRef
                // örneğini paylaşıyor ve paylaşılan örnek önceki satırdan sessizce kopuyordu (boş articles).
                Articles = c.Articles.Select(a => a with { }).ToList(),
                SectionPath = article?.SectionPath ?? string.Empty,
                Heading = article?.Heading ?? string.Empty,
                ChunkStrategy = strategy,
                ChunkIndex = index,
                Content = c.Text, // embed edilen metnin aynısı (C/D'de başlık dahil)
                TokenCount = c.TokenCount,
                Embedding = new Vector(vectors[i]),
            };
        }).ToList();

        await using var tx = await db.Database.BeginTransactionAsync();
        await db.KnowledgeChunks.Where(k => k.ChunkStrategy == strategy).ExecuteDeleteAsync();
        db.KnowledgeChunks.AddRange(entities);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        db.ChangeTracker.Clear();

        return entities.Count;
    }

    // SDK'nın kendi retry'ı birkaç saniye bekler; Azure TPM kotası dolunca (429) ~1 dk beklemek gerekir.
    private async Task<GeneratedEmbeddings<Embedding<float>>> GenerateWithRetryAsync(List<string> texts, int maxAttempts = 5)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await generator.GenerateAsync(texts);
            }
            catch (ClientResultException ex) when (ex.Status == 429 && attempt < maxAttempts)
            {
                var wait = RetryAfter(ex) ?? TimeSpan.FromSeconds(60);
                Console.WriteLine($"  429 kota aşıldı, {wait.TotalSeconds:F0} sn bekleniyor (deneme {attempt}/{maxAttempts})");
                await Task.Delay(wait);
            }
        }
    }

    private static TimeSpan? RetryAfter(ClientResultException ex) =>
        ex.GetRawResponse()?.Headers.TryGetValue("retry-after", out var value) == true && int.TryParse(value, out var seconds)
            ? TimeSpan.FromSeconds(seconds + 1)
            : null;

    private ParsedArticle Find(string law, ArticleRef r) =>
        articlesByLaw[law].Single(a => a.ArticleType == r.Type && a.ArticleNo == r.No);
}
