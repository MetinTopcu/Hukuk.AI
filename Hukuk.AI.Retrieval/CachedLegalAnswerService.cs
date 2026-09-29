using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Hukuk.AI.Retrieval;

// Canlı akış: soru embedding'i (cache'li) -> semantic cache (aday + LLM doğrulama) -> yoksa RAG cevabı -> cache'e yaz.
// Aynı soru aynı anda gelirse tek RAG çağrısı yapılır (InflightAnswers). LegalAnswerService cache'siz kalır;
// eval cevap kalitesini cache'ten bağımsız ölçer.
public class CachedLegalAnswerService(LegalAnswerService answers, QueryEmbedder embedder, SemanticAnswerCache cache,
    InflightAnswers inflight, ILogger<CachedLegalAnswerService> logger)
{
    // onDelta: streaming için cevap parçaları. Cache'ten veya bekleyen istek olarak gelen cevap tek parça gönderilir.
    public async Task<(LegalAnswer Answer, CacheMatch? Cache)> AnswerAsync(string question, Func<string, Task>? onDelta = null, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        // Aynı vektör aramada da kullanılır; ikinci istek HybridCache L1'den (bellekten) gelir.
        var vector = await embedder.EmbedAsync(question, cancellationToken);

        if (await cache.FindAsync(question, vector, cancellationToken) is { } hit)
        {
            logger.LogInformation("Cevap cache'ten: {TotalMs} ms", sw.ElapsedMilliseconds);
            if (onDelta is not null)
                await onDelta(hit.Answer.Answer);
            return (hit.Answer, hit.Match);
        }

        var (task, isLeader) = inflight.GetOrStart(QueryEmbedder.Normalize(question), async () =>
        {
            // Lider isteği iptal olsa (kullanıcı sayfayı kapatsa) bile bekleyenler için üretim sürer: CancellationToken.None.
            var answer = await answers.AnswerAsync(question, onDelta is null ? null : SafeDelta(onDelta), CancellationToken.None);
            await cache.StoreAsync(question, vector, answer);
            return answer;
        });

        if (!isLeader)
        {
            logger.LogInformation("Aynı soru zaten cevaplanıyor, bekleniyor");
            var shared = await task.WaitAsync(cancellationToken);
            if (onDelta is not null)
                await onDelta(shared.Answer);
            logger.LogInformation("Cevap bekleyen istek olarak alındı: {TotalMs} ms", sw.ElapsedMilliseconds);
            return (shared, null);
        }

        // Lider iptal edilmeden bekler: üretim bu isteğin scope'undaki DbContext'i kullanıyor, erken dispose olmasın.
        var result = await task;
        logger.LogInformation("Cevap üretildi: toplam {TotalMs} ms", sw.ElapsedMilliseconds);
        return (result, null);
    }

    // Kullanıcı bağlantıyı kapatırsa yazma hatası üretimi (ve bekleyen diğer istekleri) düşürmesin.
    private Func<string, Task> SafeDelta(Func<string, Task> onDelta)
    {
        var broken = false;
        return async text =>
        {
            if (broken)
                return;
            try { await onDelta(text); }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                broken = true;
                logger.LogInformation("İstemci bağlantısı kapandı, cevap üretimi bekleyenler için sürüyor");
            }
        };
    }
}
