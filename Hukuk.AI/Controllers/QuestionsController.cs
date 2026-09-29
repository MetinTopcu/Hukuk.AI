using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Hukuk.AI.Retrieval;
using Microsoft.AspNetCore.Mvc;

namespace Hukuk.AI.Controllers;

// Ortak hukuk bilgi tabanı üzerinden soru-cevap (RAG).
[ApiController]
[Route("api/questions")]
public class QuestionsController(CachedLegalAnswerService answers, ILogger<QuestionsController> logger) : ControllerBase
{
    public const string Disclaimer = "Bu cevap genel bilgilendirme amaçlıdır, hukuki danışmanlık yerine geçmez.";

    public record AskRequest([Required, StringLength(2000, MinimumLength = 3)] string Question);

    // Cache: cevap semantic cache'ten geldiyse eşleşen eski soru ve benzerliği, yoksa null.
    public record AskResponse(string Answer, List<KnowledgeSource> Citations, string Disclaimer, CacheMatch? Cache);

    [HttpPost]
    public async Task<AskResponse> Ask(AskRequest request, CancellationToken cancellationToken)
    {
        var (result, cache) = await answers.AnswerAsync(request.Question.Trim(), cancellationToken: cancellationToken);
        return new AskResponse(result.Answer, result.Citations, Disclaimer, cache);
    }

    // Server-Sent Events: cevap üretildikçe "delta" olayları ({text}), sonunda "done" ({citations, disclaimer, cache}).
    // Atıflar cevabın tamamına bakılarak seçildiği için sonda gelir. Hata olursa "error" olayı.
    [HttpPost("stream")]
    public async Task Stream(AskRequest request, CancellationToken cancellationToken)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";

        async Task Send(string eventName, object data)
        {
            await Response.WriteAsync($"event: {eventName}\ndata: {JsonSerializer.Serialize(data, JsonOptions)}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }

        try
        {
            var (result, cache) = await answers.AnswerAsync(request.Question.Trim(), text => Send("delta", new { text }), cancellationToken);
            await Send("done", new { result.Citations, Disclaimer, cache });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Streaming cevap hatası");
            await Send("error", new { message = "Cevap üretilemedi." });
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
