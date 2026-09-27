using System.ComponentModel.DataAnnotations;
using Hukuk.AI.Retrieval;
using Microsoft.AspNetCore.Mvc;

namespace Hukuk.AI.Controllers;

// Ortak hukuk bilgi tabanı üzerinden soru-cevap (RAG).
[ApiController]
[Route("api/questions")]
public class QuestionsController(LegalAnswerService answers) : ControllerBase
{
    public const string Disclaimer = "Bu cevap genel bilgilendirme amaçlıdır, hukuki danışmanlık yerine geçmez.";

    public record AskRequest([Required, StringLength(2000, MinimumLength = 3)] string Question);

    public record AskResponse(string Answer, List<KnowledgeSource> Citations, string Disclaimer);

    [HttpPost]
    public async Task<AskResponse> Ask(AskRequest request, CancellationToken cancellationToken)
    {
        var result = await answers.AnswerAsync(request.Question.Trim(), cancellationToken);
        return new AskResponse(result.Answer, result.Citations, Disclaimer);
    }
}
