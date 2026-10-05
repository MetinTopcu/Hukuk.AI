using System.Text.Json;
using Hukuk.AI.Retrieval;
using Microsoft.Extensions.Logging;
using NRedisStack;
using NRedisStack.RedisStackCommands;
using NRedisStack.Search;
using NRedisStack.Search.Literals.Enums;
using StackExchange.Redis;

namespace Hukuk.AI.Documents;

public static class DocumentStatus
{
    public const string Queued = "queued";         // yüklendi, kuyrukta
    public const string Processing = "processing"; // worker aldı (OCR, parçalama, embedding)
    public const string Analyzing = "analyzing";   // parçalar hazır, risk raporu üretiliyor
    public const string Ready = "ready";           // rapor hazır, soru sorulabilir
    public const string Failed = "failed";

    public static bool IsFinal(string status) => status is Ready or Failed;
}

// Dosyanın kendisi hariç belge bilgisi (API cevabı ve Pub/Sub mesajı). Pages/Chunks işlenince dolar.
public record DocumentInfo(string Id, string FileName, string ContentType, long Size, DateTimeOffset CreatedAt,
    string Status, string? Error, int? Pages, int? Chunks);

// Yüklenen belgeler Redis'te oturumluk tutulur (kararlar 2026-09-30): kalıcı kopya yok (KVKK), kullanılmayan belge
// 2 saat sonra kendiliğinden silinir; her okumada süre baştan başlar (kayan TTL, parçalar dahil).
//   doc:{id}               HASH  bilgi alanları + "file" (ham dosya; işlenince silinir) + "report" (risk raporu JSON'u)
//   docchunk:{id}:{n}      HASH  parça: doc_id (TAG), label, heading, page, content, token_count, embedding
//   doc:{id}:status        Pub/Sub kanalı: durum her değiştiğinde DocumentInfo JSON'u (SSE bunu dinler)
// Kuyruk: bkz. DocumentQueue.
// Id tahmin edilemez (128 bit rastgele): kullanıcı/oturum yönetimi gelene kadar belgeye erişim anahtarı id'nin kendisi.
public class DocumentStore(IConnectionMultiplexer redis, ILogger<DocumentStore> logger)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(2);

    public const string ChunkIndexName = "idx:docchunks";
    private const string ChunkKeyPrefix = "docchunk:";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly RedisValue[] InfoFields = ["fileName", "contentType", "size", "createdAt", "status", "error", "pages", "chunkCount"];

    private IDatabase Db => redis.GetDatabase();

    private static string Key(string id) => $"doc:{id}";

    private static string ChunkKey(string id, int index) => $"{ChunkKeyPrefix}{id}:{index}";

    public static RedisChannel StatusChannel(string id) => RedisChannel.Literal($"doc:{id}:status");

    // Parça index'i (karar 2026-09-30): HNSW + doc_id TAG filtresi; arama hep tek belge içinde.
    // Vektör ayarları semantic cache ile aynı: FLOAT16, COSINE, 1536 (bilgi tabanıyla aynı embedding modeli),
    // M 16 / EF_CONSTRUCTION 200 / EF_RUNTIME 10.
    public async Task EnsureIndexAsync()
    {
        var schema = new Schema()
            .AddTagField("doc_id")
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
            await Db.FT().CreateAsync(ChunkIndexName, new FTCreateParams().On(IndexDataType.HASH).Prefix(ChunkKeyPrefix), schema);
            logger.LogInformation("Redis index {Index} oluşturuldu", ChunkIndexName);
        }
        catch (RedisServerException e) when (e.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            // Index şeması değişirse elle silinmeli: FT.DROPINDEX idx:docchunks
        }
    }

    // Bilgi + dosya kaydı ve kuyruk kaydı tek transaction'da: ya ikisi de yazılır ya hiçbiri (kuyrukta dosyasız iş kalmaz).
    public async Task<DocumentInfo> AddAsync(string fileName, string contentType, byte[] content)
    {
        var info = new DocumentInfo(Guid.NewGuid().ToString("N"), fileName, contentType, content.Length,
            DateTimeOffset.UtcNow, DocumentStatus.Queued, null, null, null);

        var tx = Db.CreateTransaction();
        _ = tx.HashSetAsync(Key(info.Id),
        [
            new("fileName", info.FileName),
            new("contentType", info.ContentType),
            new("size", info.Size),
            new("createdAt", info.CreatedAt.ToUnixTimeMilliseconds()),
            new("status", info.Status),
            new("file", content),
        ]);
        _ = tx.KeyExpireAsync(Key(info.Id), Ttl);
        DocumentQueue.Enqueue(tx, info.Id);
        if (!await tx.ExecuteAsync())
            throw new InvalidOperationException("Belge kaydedilemedi.");
        return info;
    }

    // Belge yoksa (hiç olmadı veya TTL doldu) null. Okuma belgenin ve parçalarının süresini uzatır.
    public async Task<DocumentInfo?> GetAsync(string id)
    {
        var info = await ReadInfoAsync(id);
        if (info is not null)
            await TouchAsync(id, info.Chunks ?? 0);
        return info;
    }

    // Worker için: bilgi + ham dosya. Dosya yoksa (belge silinmiş ya da zaten işlenmiş) null.
    public async Task<(DocumentInfo Info, byte[] Content)?> GetFileAsync(string id)
    {
        var info = await ReadInfoAsync(id);
        var file = await Db.HashGetAsync(Key(id), "file");
        return info is null || file.IsNull ? null : (info, (byte[])file!);
    }

    // Aynı belgenin kaçıncı kez işlenmeye başladığı (worker çökerse iş tekrar alınır; belge çökertiyorsa sonsuz döngü olmasın).
    public Task<long> IncrementAttemptsAsync(string id) => Db.HashIncrementAsync(Key(id), "attempts");

    // Parçalar + belge alanları tek transaction'da; ham dosya silinir (artık gerekmiyor, KVKK: en az veri).
    public async Task SaveChunksAsync(string id, int pages, IReadOnlyList<DocumentChunk> chunks, IReadOnlyList<float[]> vectors)
    {
        var tx = Db.CreateTransaction();
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            _ = tx.HashSetAsync(ChunkKey(id, c.Index),
            [
                new("doc_id", id),
                new("label", c.Label),
                new("heading", c.Heading ?? ""),
                new("page", c.Page),
                new("content", c.Content),
                new("token_count", c.TokenCount),
                new("embedding", SemanticAnswerCache.ToFloat16(vectors[i])),
            ]);
            _ = tx.KeyExpireAsync(ChunkKey(id, c.Index), Ttl);
        }
        _ = tx.HashSetAsync(Key(id), [new("pages", pages), new("chunkCount", chunks.Count)]);
        _ = tx.HashDeleteAsync(Key(id), "file");
        _ = tx.KeyExpireAsync(Key(id), Ttl);
        if (!await tx.ExecuteAsync())
            throw new InvalidOperationException("Belge parçaları kaydedilemedi.");
    }

    // Belgenin tüm parçaları, belge sırasıyla (embedding hariç). Süresi dolmuş parça atlanır.
    public async Task<List<DocumentChunk>> GetChunksAsync(string id, int chunkCount)
    {
        RedisValue[] fields = ["label", "heading", "page", "content", "token_count"];
        var batch = Db.CreateBatch();
        var tasks = Enumerable.Range(0, chunkCount).Select(i => batch.HashGetAsync(ChunkKey(id, i), fields)).ToList();
        batch.Execute();
        var rows = await Task.WhenAll(tasks);
        return rows.Select((v, i) => (v, i)).Where(x => !x.v[3].IsNull)
            .Select(x => new DocumentChunk(x.i, x.v[0]!, x.v[1].IsNullOrEmpty ? null : x.v[1].ToString(), (int)x.v[2], x.v[3]!, (int)x.v[4]))
            .ToList();
    }

    // Soruya en yakın parçalar (sadece bu belgede: doc_id TAG filtresi), yakından uzağa. Index: parçanın belgedeki sırası.
    // id sorguya girmeden önce var olan bir belgeye ait olduğu doğrulanmış olmalı (bkz. GetAsync).
    public async Task<List<(int Index, double Similarity)>> SearchChunksAsync(string id, float[] questionVector, int count)
    {
        var query = new Query($"(@doc_id:{{{id}}})=>[KNN {count} @embedding $vec AS distance]")
            .AddParam("vec", SemanticAnswerCache.ToFloat16(questionVector))
            .ReturnFields("distance")
            .SetSortBy("distance")
            .Limit(0, count)
            .Dialect(2);
        var result = await Db.FT().SearchAsync(ChunkIndexName, query);
        // COSINE'de Redis mesafe döner: benzerlik = 1 - mesafe.
        return result.Documents
            .Select(d => (int.Parse(d.Id[(d.Id.LastIndexOf(':') + 1)..]),
                1 - double.Parse(d["distance"]!, System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
    }

    // Rapor belgeyle aynı anahtarda: süresi belgeyle birlikte dolar.
    public Task SaveReportAsync(string id, RiskReport report) =>
        Db.HashSetAsync(Key(id), "report", JsonSerializer.Serialize(report, JsonOptions));

    // Rapor henüz üretilmediyse (veya belge yoksa) null. Okuma belgenin süresini uzatır.
    public async Task<RiskReport?> GetReportAsync(string id)
    {
        var report = await Db.HashGetAsync(Key(id), "report");
        if (report.IsNull || await GetAsync(id) is null)
            return null;
        return JsonSerializer.Deserialize<RiskReport>(report.ToString(), JsonOptions);
    }

    // Durumu yazar ve dinleyenlere yayınlar. Belge bu arada silindiyse (TTL) null.
    public async Task<DocumentInfo?> SetStatusAsync(string id, string status, string? error = null)
    {
        if (!await Db.KeyExistsAsync(Key(id)))
            return null;
        await Db.HashSetAsync(Key(id), "status", status);
        if (error is null)
            await Db.HashDeleteAsync(Key(id), "error");
        else
            await Db.HashSetAsync(Key(id), "error", error);
        var info = await GetAsync(id);
        if (info is not null)
            await Db.PublishAsync(StatusChannel(id), JsonSerializer.Serialize(info, JsonOptions));
        return info;
    }

    public static DocumentInfo ParseStatusMessage(RedisValue message) =>
        JsonSerializer.Deserialize<DocumentInfo>(message.ToString(), JsonOptions)!;

    private async Task<DocumentInfo?> ReadInfoAsync(string id)
    {
        var v = await Db.HashGetAsync(Key(id), InfoFields);
        if (v[0].IsNull)
            return null;
        return new DocumentInfo(id, v[0]!, v[1]!, (long)v[2], DateTimeOffset.FromUnixTimeMilliseconds((long)v[3]),
            v[4]!, v[5], (int?)v[6], (int?)v[7]);
    }

    // Kayan TTL: belge ve tüm parçaları birlikte uzar (parçalar belgeden önce düşmesin).
    private async Task TouchAsync(string id, int chunkCount)
    {
        var batch = Db.CreateBatch();
        var tasks = new List<Task> { batch.KeyExpireAsync(Key(id), Ttl) };
        for (var i = 0; i < chunkCount; i++)
            tasks.Add(batch.KeyExpireAsync(ChunkKey(id, i), Ttl));
        batch.Execute();
        await Task.WhenAll(tasks);
    }
}
