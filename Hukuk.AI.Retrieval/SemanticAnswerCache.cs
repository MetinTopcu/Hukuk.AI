using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NRedisStack;
using NRedisStack.RedisStackCommands;
using NRedisStack.Search;
using NRedisStack.Search.Literals.Enums;
using StackExchange.Redis;

namespace Hukuk.AI.Retrieval;

// MinSimilarity: aday eşiği; geçen aday LLM doğrulayıcısına gider.
// IndexName/KeyPrefix: eval eşik ölçümünü canlı cache'ten ayrı bir index'te yapar.
public record SemanticCacheOptions(double MinSimilarity, string IndexName = "idx:answers", string KeyPrefix = "answer:");

// Cache'ten dönen cevabın hangi soruya verildiği ve ne kadar benzediği (şeffaflık + eşik ayarı için).
public record CacheMatch(string Question, double Similarity);

// Semantic cache: yeni sorunun vektörüne en yakın eski soru bulunur (Redis vektör araması); benzerlik aday eşiğini
// geçer ve LLM doğrulayıcısı "aynı cevap" derse onun cevabı döner (arama + cevap üretimi yok).
// Aynı kelimelerle değil, aynı anlamla sorulan soruyu yakalar. Sadece eşik güvenli değil: bkz. CacheMatchVerifier.
// Kararlar (2026-09-29): HNSW (veri büyüyecek varsayımı), COSINE, 1536 boyut (sorgu embedding'i tekrar kullanılır),
// FLOAT16 (bellek yarıya iner), HASH (vektör binary saklanır), M 16 / EF_CONSTRUCTION 200 / EF_RUNTIME 10 (Redis
// varsayılanları; sadece en yakın 1 kayıt istenir), TTL 7 gün (mevzuat değişebilir).
// Bilgi tabanı, arama ayarları veya cevap prompt'u değişince eski cevaplar dönmesin diye kayıtlar sürümle etiketlenir
// ve arama sadece güncel sürümde yapılır (TAG filtresi); eski kayıtlar TTL ile kendiliğinden silinir.
// Redis erişilemezse cache atlanır, soru normal yoldan cevaplanır (cache hiçbir zaman isteği düşürmez).
public class SemanticAnswerCache(IConnectionMultiplexer redis, CacheMatchVerifier verifier, SemanticCacheOptions options, ILogger<SemanticAnswerCache> logger)
{
    // Bilgi tabanı (yeni kanun, yeniden chunk'lama) veya arama pipeline'ı değişince artır.
    // 1: TBK kira + İş K., strateji D, bütçe 4000, yeniden yazımsız.
    public const int KnowledgeBaseVersion = 1;

    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    private static string KbTag => $"kb{KnowledgeBaseVersion}";
    private static string PromptTag => $"p{LegalAnswerService.PromptVersion}";

    private IDatabase Db => redis.GetDatabase();

    public async Task EnsureIndexAsync()
    {
        var schema = new Schema()
            .AddTagField("kb_version")
            .AddTagField("prompt_version")
            .AddVectorField("embedding", Schema.VectorField.VectorAlgo.HNSW, new Dictionary<string, object>
            {
                ["TYPE"] = "FLOAT16",
                ["DIM"] = 1536,
                ["DISTANCE_METRIC"] = "COSINE",
                ["M"] = 16,
                ["EF_CONSTRUCTION"] = 200,
                ["EF_RUNTIME"] = 10,
            });
        try
        {
            await Db.FT().CreateAsync(options.IndexName, new FTCreateParams().On(IndexDataType.HASH).Prefix(options.KeyPrefix), schema);
            logger.LogInformation("Redis index {Index} oluşturuldu", options.IndexName);
        }
        catch (RedisServerException e) when (e.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            // Index şeması değişirse elle silinmeli: FT.DROPINDEX idx:answers
        }
    }

    // Index ve kayıtları siler (eval sonunda geçici index için).
    public Task DropIndexAsync() => Db.FT().DropIndexAsync(options.IndexName, dd: true);

    public async Task<(LegalAnswer Answer, CacheMatch Match)?> FindAsync(string question, float[] questionVector, CancellationToken cancellationToken = default)
    {
        (string Question, string AnswerJson, double Similarity)? match;
        try
        {
            match = await NearestAsync(questionVector);
        }
        catch (RedisException e)
        {
            logger.LogWarning(e, "Semantic cache okunamadı, atlanıyor");
            return null;
        }

        if (match is null || match.Value.Similarity < options.MinSimilarity)
        {
            logger.LogInformation("Semantic cache miss: aday yok (en yakın benzerlik {Similarity:F3})", match?.Similarity);
            return null;
        }
        // Birebir aynı soru (boşluk farkı hariç) doğrulamaya gerek duymaz (~1 sn kazanç).
        var identical = QueryEmbedder.Normalize(match.Value.Question) == QueryEmbedder.Normalize(question);
        if (!identical && !await verifier.IsSameAnswerAsync(match.Value.Question, question, cancellationToken))
        {
            logger.LogInformation("Semantic cache miss: doğrulayıcı reddetti (benzerlik {Similarity:F3})", match.Value.Similarity);
            return null;
        }

        logger.LogInformation("Semantic cache hit (benzerlik {Similarity:F3})", match.Value.Similarity);
        var answer = JsonSerializer.Deserialize<LegalAnswer>(match.Value.AnswerJson)!;
        return (answer, new CacheMatch(match.Value.Question, match.Value.Similarity));
    }

    // En yakın kayıt, eşik uygulanmadan (eval eşiği bununla ölçer).
    public async Task<(string Question, string AnswerJson, double Similarity)?> NearestAsync(float[] questionVector)
    {
        var query = new Query($"(@kb_version:{{{KbTag}}} @prompt_version:{{{PromptTag}}})=>[KNN 1 @embedding $vec AS distance]")
            .AddParam("vec", ToFloat16(questionVector))
            .ReturnFields("distance", "question", "answer")
            .SetSortBy("distance")
            .Limit(0, 1)
            .Dialect(2);
        var result = await Db.FT().SearchAsync(options.IndexName, query);
        if (result.Documents.Count == 0)
            return null;

        var doc = result.Documents[0];
        // COSINE'de Redis mesafe döner: benzerlik = 1 - mesafe.
        // FLOAT16 yuvarlaması aynı vektörde mesafeyi hafif negatif yapabiliyor; benzerlik en fazla 1.
        var similarity = Math.Min(1, 1 - double.Parse(doc["distance"]!, CultureInfo.InvariantCulture));
        return (doc["question"]!, doc["answer"]!, similarity);
    }

    public async Task StoreAsync(string question, float[] questionVector, LegalAnswer answer)
    {
        try
        {
            // Aynı soru (aynı sürümlerde) tekrar yazılırsa üzerine yazar, çift kayıt olmaz.
            var key = $"{options.KeyPrefix}{KbTag}:{PromptTag}:{QueryEmbedder.Hash(QueryEmbedder.Normalize(question))}";
            var transaction = Db.CreateTransaction(); // HSET + EXPIRE birlikte: TTL'siz kayıt kalmasın (volatile-lru siler)
            _ = transaction.HashSetAsync(key,
            [
                new("question", question),
                new("answer", JsonSerializer.Serialize(answer)),
                new("embedding", ToFloat16(questionVector)),
                new("kb_version", KbTag),
                new("prompt_version", PromptTag),
            ]);
            _ = transaction.KeyExpireAsync(key, Jitter.Apply(Ttl)); // ±%10: toplu süre dolması olmasın
            await transaction.ExecuteAsync();
        }
        catch (RedisException e)
        {
            logger.LogWarning(e, "Semantic cache yazılamadı, atlanıyor");
        }
    }

    // Index TYPE FLOAT16: her boyut 2 bayt (little-endian IEEE half).
    public static byte[] ToFloat16(float[] vector)
    {
        var halves = new Half[vector.Length];
        for (var i = 0; i < vector.Length; i++)
            halves[i] = (Half)vector[i];
        return MemoryMarshal.AsBytes(halves.AsSpan()).ToArray();
    }
}
