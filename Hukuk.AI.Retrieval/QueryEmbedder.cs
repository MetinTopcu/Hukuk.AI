using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Hukuk.AI.Retrieval;

// Soru embedding'i, cache-aside ile (HybridCache: L1 süreç belleği + L2 Redis).
// Aynı soru tekrar gelince Azure'a gidilmez (süre + 429 riski). HybridCache aynı anahtar için eşzamanlı istekleri
// tek çağrıda birleştirir (cache stampede koruması); L1 sayesinde aynı istekte ikinci kez istenen vektör Redis'e de gitmez.
public partial class QueryEmbedder(IEmbeddingGenerator<string, Embedding<float>> generator, HybridCache cache, ILogger<QueryEmbedder> logger)
{
    // Model veya boyut değişirse artır: eski vektörler yeni modelinkilerle karşılaştırılamaz.
    public const string ModelVersion = "te3l-1536";

    // TTL jitter (±%10): aynı anda yazılan kayıtlar (ör. toplu ısıtma) aynı anda düşüp Azure'a yığılmasın.
    private static HybridCacheEntryOptions Options() => new()
    {
        Expiration = Jitter.Apply(TimeSpan.FromDays(30)),
        LocalCacheExpiration = TimeSpan.FromMinutes(10),
    };

    public async Task<float[]> EmbedAsync(string question, CancellationToken cancellationToken = default)
    {
        var text = Normalize(question);
        var miss = false;
        // byte[] HybridCache'te serileştirilmeden saklanır (FLOAT32, 1536 x 4 = 6 KB).
        var bytes = await cache.GetOrCreateAsync($"emb:{ModelVersion}:{Hash(text)}", async ct =>
        {
            miss = true;
            var vector = await generator.GenerateVectorAsync(text, cancellationToken: ct);
            return MemoryMarshal.AsBytes(vector.Span).ToArray();
        }, Options(), cancellationToken: cancellationToken);

        logger.LogInformation("Embedding cache {Result}", miss ? "miss" : "hit");
        return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }

    // Sadece anlamı değiştirmeyen farklar: baş/son boşluk, çoklu boşluk. Büyük/küçük harf korunur
    // (embedding'i değiştirebilir; eval'deki vektörlerle aynı kalsın).
    public static string Normalize(string question) => Whitespace().Replace(question.Trim(), " ");

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
