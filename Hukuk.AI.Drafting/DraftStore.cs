using System.Text.Json;
using Hukuk.AI.Documents;
using StackExchange.Redis;

namespace Hukuk.AI.Drafting;

// Fields: kullanıcıdan toplanan bilgiler (DraftField.Key → değer). Result: taslak yazıldıysa son hali.
public record DraftSession(string Id, string Type, string Request, Dictionary<string, string> Fields,
    DateTimeOffset CreatedAt, DraftResult? Result);

// Taslak oturumu Redis'te tutulur (karar 2026-10-05): ilk istek bilgileri çıkarır ve eksikleri sorar, sonraki istekler
// sadece id ile devam eder. Yüklenen belgelerle aynı desen: kalıcı kopya yok, 2 saat kayan TTL.
//   draft:{id}   HASH  type, request, fields (JSON), createdAt, result (JSON)
// Id tahmin edilemez (128 bit rastgele): kullanıcı yönetimi gelene kadar erişim anahtarı id'nin kendisi.
public class DraftStore(IConnectionMultiplexer redis)
{
    public static readonly TimeSpan Ttl = DocumentStore.Ttl;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly RedisValue[] SessionFields = ["type", "request", "fields", "createdAt", "result"];

    private IDatabase Db => redis.GetDatabase();

    private static string Key(string id) => $"draft:{id}";

    public async Task<DraftSession> CreateAsync(string type, string request, Dictionary<string, string> fields)
    {
        var session = new DraftSession(Guid.NewGuid().ToString("N"), type, request, fields, DateTimeOffset.UtcNow, null);
        await WriteAsync(session.Id,
        [
            new("type", session.Type),
            new("request", session.Request),
            new("fields", JsonSerializer.Serialize(session.Fields, JsonOptions)),
            new("createdAt", session.CreatedAt.ToUnixTimeMilliseconds()),
        ]);
        return session;
    }

    // Oturum yoksa (hiç olmadı veya TTL doldu) null. Okuma süreyi uzatır.
    public async Task<DraftSession?> GetAsync(string id)
    {
        var v = await Db.HashGetAsync(Key(id), SessionFields);
        if (v[0].IsNull)
            return null;
        await Db.KeyExpireAsync(Key(id), Ttl);
        return new DraftSession(id, v[0]!, v[1]!,
            JsonSerializer.Deserialize<Dictionary<string, string>>(v[2].ToString(), JsonOptions)!,
            DateTimeOffset.FromUnixTimeMilliseconds((long)v[3]),
            v[4].IsNull ? null : JsonSerializer.Deserialize<DraftResult>(v[4].ToString(), JsonOptions));
    }

    public Task SaveFieldsAsync(string id, Dictionary<string, string> fields) =>
        WriteAsync(id, [new("fields", JsonSerializer.Serialize(fields, JsonOptions))]);

    public Task SaveResultAsync(string id, DraftResult result) =>
        WriteAsync(id, [new("result", JsonSerializer.Serialize(result, JsonOptions))]);

    // Yazma ve süre tek transaction'da: anahtar hiçbir zaman süresiz kalmaz.
    private async Task WriteAsync(string id, HashEntry[] entries)
    {
        var tx = Db.CreateTransaction();
        _ = tx.HashSetAsync(Key(id), entries);
        _ = tx.KeyExpireAsync(Key(id), Ttl);
        if (!await tx.ExecuteAsync())
            throw new InvalidOperationException("Taslak oturumu kaydedilemedi.");
    }
}
