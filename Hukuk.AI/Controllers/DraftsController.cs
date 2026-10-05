using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Hukuk.AI.Drafting;
using Microsoft.AspNetCore.Mvc;

namespace Hukuk.AI.Controllers;

// Sözleşme taslağı: kullanıcı isteğini serbest metinle yazar, eksik bilgiler soru olarak döner, bilgiler tamamlanınca
// taslak stream edilir. Oturum Redis'te (bkz. DraftStore).
[ApiController]
[Route("api/drafts")]
public class DraftsController(DraftStore store, DraftIntake intake, DraftWriter writer, ILogger<DraftsController> logger) : ControllerBase
{
    public const string Disclaimer = "Bu taslak genel bilgilendirme amaçlıdır, hukuki danışmanlık yerine geçmez; imzalamadan önce bir avukata danışın.";

    public static class Status
    {
        public const string NeedsInfo = "needs_info"; // zorunlu bilgi eksik: Questions cevaplanmalı
        public const string Ready = "ready";          // taslak yazılabilir (stream)
        public const string Done = "done";            // taslak yazıldı: Result dolu
    }

    public record StartRequest([Required, StringLength(4000, MinimumLength = 10)] string Request);

    // Answers: DraftQuestion.Field → cevap. Daha önce verilmiş bir bilgi de aynı yolla değiştirilebilir.
    public record AnswersRequest([Required] Dictionary<string, string> Answers);

    public record DraftQuestion(string Field, string Label, string Question);

    public record DraftResponse(string Id, string Type, string TypeName, string Status, Dictionary<string, string> Fields,
        List<DraftQuestion> Questions, DraftResult? Result, string Disclaimer);

    [HttpPost]
    public async Task<ActionResult<DraftResponse>> Start(StartRequest request, CancellationToken cancellationToken)
    {
        var (template, fields) = await intake.ExtractAsync(request.Request.Trim(), cancellationToken);
        if (template is null)
            return Problem($"Şimdilik sadece şu taslaklar hazırlanabiliyor: {string.Join(", ", DraftTemplates.All.Select(t => t.Name))}.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        var session = await store.CreateAsync(template.Type, request.Request.Trim(), fields);
        return CreatedAtAction(nameof(Get), new { id = session.Id }, ToResponse(session, template));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<DraftResponse>> Get(string id) =>
        await store.GetAsync(id) is { } session ? ToResponse(session, DraftTemplates.Find(session.Type)!) : NotFound();

    [HttpPost("{id}/answers")]
    public async Task<ActionResult<DraftResponse>> Answer(string id, AnswersRequest request)
    {
        var session = await store.GetAsync(id);
        if (session is null)
            return NotFound();

        var template = DraftTemplates.Find(session.Type)!;
        foreach (var (key, value) in DraftIntake.Clean(template, request.Answers))
            session.Fields[key] = value;
        await store.SaveFieldsAsync(id, session.Fields);
        return ToResponse(session, template);
    }

    // Server-Sent Events: taslak yazıldıkça "delta" ({text}), yazım bitince "checking" (kanuna uygunluk denetimi
    // başladı, ~30 sn), düzeltilen her madde için "revision" ({clause, title, text, reason, citations}), sonunda "done"
    // ({draft, revisions, unresolved, basis, citations, disclaimer}). Hata olursa "error".
    // Oturum yoksa 404, zorunlu bilgi eksikse 409 (olay akışı başlamadan).
    [HttpPost("{id}/stream")]
    public async Task Stream(string id, CancellationToken cancellationToken)
    {
        var session = await store.GetAsync(id);
        var template = session is null ? null : DraftTemplates.Find(session.Type);
        if (session is null || template is null || template.Missing(session.Fields).Count > 0)
        {
            Response.StatusCode = session is null ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict;
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
            var events = new DraftEvents(text => Send("delta", new { text }), () => Send("checking", new { }), revision => Send("revision", revision));
            var result = await writer.WriteAsync(template, session.Fields, events, cancellationToken);
            await store.SaveResultAsync(id, result);
            await Send("done", new { result.Draft, result.Revisions, result.Unresolved, result.Basis, result.Citations, Disclaimer });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Taslak streaming hatası");
            await Send("error", new { message = "Taslak üretilemedi." });
        }
    }

    private static DraftResponse ToResponse(DraftSession session, DraftTemplate template)
    {
        var questions = template.Missing(session.Fields).Select(f => new DraftQuestion(f.Key, f.Label, f.Question)).ToList();
        var status = questions.Count > 0 ? Status.NeedsInfo : session.Result is null ? Status.Ready : Status.Done;
        return new DraftResponse(session.Id, template.Type, template.Name, status, session.Fields, questions, session.Result, Disclaimer);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
