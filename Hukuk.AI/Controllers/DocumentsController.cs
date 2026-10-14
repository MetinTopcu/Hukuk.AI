using System.Text.Json;
using Hukuk.AI.Credits;
using Hukuk.AI.Documents;
using Hukuk.AI.Retrieval;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace Hukuk.AI.Controllers;

// Kullanıcının yüklediği belge (sözleşme, dilekçe...): yükleme hemen 202 döner, işleme arka planda (Redis Streams
// kuyruğu). İlerleme GET ile sorgulanır ya da events (SSE) ile canlı izlenir.
// Belge sadece yükleyen kullanıcıya açık: başkasının (ya da olmayan) belgesi 404.
// Kredi: yükleme ücreti (rapor dahil) yüklemede düşülür, işleme "failed" biterse worker iade eder; belge sorusu ayrı ücretli.
[Route("api/documents")]
public class DocumentsController(DocumentStore store, DocumentAnswerService answers, CreditService credits, IConnectionMultiplexer redis,
    ILogger<DocumentsController> logger) : ApiController
{
    private async Task<DocumentInfo?> FindAsync(string id) =>
        await store.IsOwnerAsync(id, UserId.ToString()) ? await store.GetAsync(id) : null;

    // 30 sayfalık taranmış PDF ~5-15 MB. Document Intelligence F0 katmanı 4 MB'tan büyük dosyayı reddeder.
    public const long MaxFileBytes = 20 * 1024 * 1024;

    // Worker takılırsa SSE bağlantısı sonsuza dek açık kalmasın.
    private static readonly TimeSpan EventsTimeout = TimeSpan.FromMinutes(10);

    [HttpPost]
    [RequestSizeLimit(MaxFileBytes + 64 * 1024)] // + multipart başlıkları
    [RequestFormLimits(MultipartBodyLengthLimit = MaxFileBytes + 64 * 1024)]
    public async Task<ActionResult<DocumentInfo>> Upload(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length == 0)
            return Problem("Dosya boş.", statusCode: StatusCodes.Status400BadRequest);
        if (file.Length > MaxFileBytes)
            return Problem($"Dosya en fazla {MaxFileBytes / 1024 / 1024} MB olabilir.", statusCode: StatusCodes.Status413PayloadTooLarge);

        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, cancellationToken);
        var content = buffer.ToArray();

        var fileName = Path.GetFileName(file.FileName);
        var contentType = DocumentFormats.Detect(fileName, content);
        if (contentType is null)
            return Problem($"Desteklenen türler: {string.Join(", ", DocumentFormats.Extensions)} (dosya içeriği uzantıyla uyuşmalı).",
                statusCode: StatusCodes.Status415UnsupportedMediaType);

        if (await credits.ReserveAsync(UserId, CreditOperations.Document, cancellationToken) is not { } charge)
            return InsufficientCredits();
        try
        {
            var info = await store.AddAsync(UserId.ToString(), charge, fileName, contentType, content);
            return AcceptedAtAction(nameof(Get), new { id = info.Id }, info);
        }
        catch
        {
            await credits.RefundAsync(charge);
            throw;
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<DocumentInfo>> Get(string id) =>
        await FindAsync(id) is { } info ? info : NotFound();

    public record ReportResponse(RiskReport Report, string Disclaimer);

    // Risk raporu belge "ready" olunca hazırdır; öncesinde 409 (belge var ama rapor henüz yok).
    [HttpGet("{id}/report")]
    public async Task<ActionResult<ReportResponse>> Report(string id)
    {
        if (await FindAsync(id) is null)
            return NotFound();
        return await store.GetReportAsync(id) is { } report
            ? new ReportResponse(report, QuestionsController.Disclaimer)
            : Problem("Rapor henüz hazır değil.", statusCode: StatusCodes.Status409Conflict);
    }

    public record AskResponse(string Answer, List<DocumentSource> DocumentCitations, List<KnowledgeSource> Citations, string Disclaimer);

    // Belge üzerinde soru-cevap: belge "ready" olunca sorulabilir; öncesinde (ya da belge işlenemediyse) 409.
    [HttpPost("{id}/questions")]
    public async Task<ActionResult<AskResponse>> Ask(string id, QuestionsController.AskRequest request, CancellationToken cancellationToken)
    {
        var info = await FindAsync(id);
        if (info is null)
            return NotFound();
        if (info.Status != DocumentStatus.Ready)
            return Problem("Belge henüz hazır değil.", statusCode: StatusCodes.Status409Conflict);

        if (await credits.ReserveAsync(UserId, CreditOperations.DocumentQuestion, cancellationToken) is not { } charge)
            return InsufficientCredits();
        try
        {
            var result = await answers.AnswerAsync(id, info.Chunks ?? 0, request.Question.Trim(), cancellationToken: cancellationToken);
            return new AskResponse(result.Answer, result.DocumentCitations, result.Citations, QuestionsController.Disclaimer);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await credits.RefundAsync(charge);
            throw;
        }
    }

    // Server-Sent Events: cevap üretildikçe "delta" olayları ({text}), sonunda "done" ({documentCitations, citations,
    // disclaimer}). Hata olursa "error" olayı. Belge yoksa 404, hazır değilse 409 (olay akışı başlamadan).
    [HttpPost("{id}/questions/stream")]
    public async Task AskStream(string id, QuestionsController.AskRequest request, CancellationToken cancellationToken)
    {
        var info = await FindAsync(id);
        if (info?.Status != DocumentStatus.Ready)
        {
            Response.StatusCode = info is null ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict;
            return;
        }
        if (await credits.ReserveAsync(UserId, CreditOperations.DocumentQuestion, cancellationToken) is not { } charge)
        {
            Response.StatusCode = StatusCodes.Status402PaymentRequired;
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";

        async Task Send(string eventName, object data)
        {
            await Response.WriteAsync($"event: {eventName}\ndata: {JsonSerializer.Serialize(data, JsonOptions)}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }

        try
        {
            var result = await answers.AnswerAsync(id, info.Chunks ?? 0, request.Question.Trim(), text => Send("delta", new { text }), cancellationToken);
            await Send("done", new { result.DocumentCitations, result.Citations, QuestionsController.Disclaimer });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Belge sorusu streaming hatası");
            await credits.RefundAsync(charge);
            await Send("error", new { message = "Cevap üretilemedi." });
        }
    }

    // Server-Sent Events: önce mevcut durum, sonra her değişiklikte "status" olayı ({DocumentInfo}); ready/failed'da biter.
    [HttpGet("{id}/events")]
    public async Task Events(string id, CancellationToken cancellationToken)
    {
        // Önce abone ol, sonra mevcut durumu oku: arada yayınlanan değişiklik kaçmaz (en kötü ihtimalle iki kez gelir).
        var subscription = await redis.GetSubscriber().SubscribeAsync(DocumentStore.StatusChannel(id));
        try
        {
            var info = await FindAsync(id);
            if (info is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(EventsTimeout);

            await Send(info, timeout.Token);
            while (!DocumentStatus.IsFinal(info.Status))
            {
                var message = await subscription.ReadAsync(timeout.Token);
                info = DocumentStore.ParseStatusMessage(message.Message);
                await Send(info, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // istemci ayrıldı veya zaman aşımı
        }
        finally
        {
            await subscription.UnsubscribeAsync();
        }
    }

    private async Task Send(DocumentInfo info, CancellationToken cancellationToken)
    {
        await Response.WriteAsync($"event: status\ndata: {JsonSerializer.Serialize(info, JsonOptions)}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
