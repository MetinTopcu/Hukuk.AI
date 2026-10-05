using Hukuk.AI.Documents;

namespace Hukuk.AI.Workers;

// Belge işleme kuyruğunu tüketen arka plan servisi; API sürecinin içinde (karar 2026-09-30). Consumer group sayesinde
// ayrı bir Worker projesine taşımak ya da birden çok kopya çalıştırmak kod değişikliği gerektirmez.
// Aynı anda tek belge işler. StackExchange.Redis bloklayan okuma (XREADGROUP BLOCK) desteklemediği için kuyruk boşken
// kısa aralıklarla yoklanır.
public class DocumentWorker(DocumentQueue queue, IServiceScopeFactory scopes, ILogger<DocumentWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(5);

    // Sabit isim: süreç yeniden başlayınca kendi yarım kalmış işini hemen geri alır.
    private static readonly string Consumer = Environment.MachineName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await queue.EnsureGroupAsync();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await queue.NextAsync(Consumer);
                if (job is null)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }

                await using (var scope = scopes.CreateAsyncScope())
                    await scope.ServiceProvider.GetRequiredService<DocumentProcessor>().ProcessAsync(job.DocumentId, stoppingToken);
                await queue.AckAsync(job);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // Redis erişilemiyor vb.: iş ACK edilmediği için kaybolmaz, bir sonraki turda tekrar alınır.
                logger.LogError(e, "Belge kuyruğu hatası");
                await Task.Delay(ErrorDelay, stoppingToken);
            }
        }
    }
}
