using StackExchange.Redis;

namespace Hukuk.AI.Documents;

public record DocumentJob(RedisValue MessageId, string DocumentId);

// İşleme kuyruğu: Redis Stream "docs:jobs" + consumer group "workers" (kararlar 2026-09-30).
// Consumer group: her iş gruptaki tek bir worker'a gider; worker XACK edene kadar iş "pending" listesinde kalır.
// Worker iş ortasında çökerse iş kaybolmaz: aynı isimle yeniden açılan worker kendi pending'ini, başka bir worker
// ise ClaimIdleAfter'dan uzun süredir bekleyen işi (XAUTOCLAIM) devralır. Kayıtta sadece belge id'si var.
public class DocumentQueue(IConnectionMultiplexer redis)
{
    public const string Stream = "docs:jobs";
    public const string Group = "workers";

    // Kuyruk sınırsız büyümesin; kayıtlar küçük, işlenmiş eski kayıtlar yaklaşık bu sayıda tutulur.
    private const int MaxLength = 10_000;

    // 30 sayfalık OCR + embedding ~1 dk sürer; bundan uzun bekleyen iş sahibinin öldüğü varsayılır.
    private static readonly TimeSpan ClaimIdleAfter = TimeSpan.FromMinutes(5);

    private IDatabase Db => redis.GetDatabase();

    // Belge kaydıyla aynı transaction'a eklenir (bkz. DocumentStore.AddAsync).
    public static void Enqueue(ITransaction tx, string documentId) =>
        _ = tx.StreamAddAsync(Stream, "id", documentId, maxLength: MaxLength, useApproximateMaxLength: true);

    // Grup ilk kez oluşturuluyorsa baştan ("0") okur: grup yokken kuyruğa girmiş işler de işlenir.
    public async Task EnsureGroupAsync()
    {
        try
        {
            await Db.StreamCreateConsumerGroupAsync(Stream, Group, "0", createStream: true);
        }
        catch (RedisServerException e) when (e.Message.StartsWith("BUSYGROUP"))
        {
        }
    }

    // Sıra: (1) bu worker'ın yarım kalmış işi, (2) ölü worker'dan kalan iş, (3) yeni iş. Hiçbiri yoksa null.
    public async Task<DocumentJob?> NextAsync(string consumer)
    {
        var own = await Db.StreamReadGroupAsync(Stream, Group, consumer, "0", count: 1);
        if (own.Length > 0 && !own[0].IsNull)
            return ToJob(own[0]);

        var claimed = await Db.StreamAutoClaimAsync(Stream, Group, consumer, (long)ClaimIdleAfter.TotalMilliseconds, "0-0", count: 1);
        if (claimed.ClaimedEntries.Length > 0 && !claimed.ClaimedEntries[0].IsNull)
            return ToJob(claimed.ClaimedEntries[0]);

        var fresh = await Db.StreamReadGroupAsync(Stream, Group, consumer, ">", count: 1);
        return fresh.Length > 0 ? ToJob(fresh[0]) : null;
    }

    public Task AckAsync(DocumentJob job) => Db.StreamAcknowledgeAsync(Stream, Group, job.MessageId);

    private static DocumentJob ToJob(StreamEntry entry) => new(entry.Id, entry["id"]!);
}
