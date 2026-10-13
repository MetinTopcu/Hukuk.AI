using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Hukuk.AI.Retrieval;

namespace Hukuk.AI.Evaluation;

// data/eval/agent-questions-v{N}.json
public record AgentQuestionSet(int Version, List<AgentQuestion> Questions);

// Expected: "kb" (sadece bilgi tabanı; web çağrılmamalı), "web" (web çağrılmalı; önce bilgi tabanına bakması hata değil),
// "kb_web" (ikisi de), "none" (hukuk dışı; hiçbir araç çağrılmamalı).
public record AgentQuestion(string Id, string Expected, string Question);

// "dotnet run -- ajan": agent modunda model doğru aracı seçiyor mu, ne kadar sürüyor? Cevap kalitesi ölçülmez
// (web sonuçları günden güne değiştiği için tekrarlanabilir değil). "-- ajan matris": reasoning minimal / low.
public static class AgentEval
{
    private static readonly string[] Matrix = [AgentAnswerService.DefaultReasoningEffort, "minimal"];

    public static async Task RunAsync(AgentAnswerService agent, string setPath, string resultsDir, bool matrix)
    {
        var set = JsonSerializer.Deserialize<AgentQuestionSet>(File.ReadAllText(setPath),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
            ?? throw new InvalidOperationException($"Soru seti okunamadı: {setPath}");
        Directory.CreateDirectory(resultsDir);

        foreach (var effort in matrix ? Matrix : Matrix[..1])
        {
            Console.WriteLine($"\n#### {effort}");
            var results = new List<Result>();
            foreach (var q in set.Questions) // sırayla: bilgi tabanı araması DbContext kullanıyor, TPM kotası düşük
            {
                var (answer, ms) = await AnswerEval.WithRetryAsync(async () =>
                {
                    var sw = Stopwatch.StartNew();
                    var a = await agent.AnswerAsync(q.Question, reasoningEffort: effort);
                    return (a, sw.ElapsedMilliseconds);
                });
                var result = new Result(q, answer, ms, IsCorrect(q.Expected, answer.ToolCalls));
                results.Add(result);
                Console.WriteLine($"  {(result.Correct ? "ok " : "YANLIŞ")} {q.Id} beklenen {q.Expected,-7} çağrılan [{string.Join(", ", answer.ToolCalls.Select(c => c.Tool))}] {ms / 1000.0:F1} sn");
            }

            Console.WriteLine($"\n{"Tür",-8}{"Doğru",8}{"Ort. çağrı",12}{"Ort. sn",9}");
            foreach (var g in results.GroupBy(r => r.Q.Expected))
                Console.WriteLine($"{g.Key,-8}{g.Count(r => r.Correct) + "/" + g.Count(),8}{g.Average(r => r.Answer.ToolCalls.Count),12:F1}{g.Average(r => r.Ms) / 1000,9:F1}");
            Console.WriteLine($"{"Toplam",-8}{results.Count(r => r.Correct) + "/" + results.Count,8}{results.Average(r => r.Answer.ToolCalls.Count),12:F1}{results.Average(r => r.Ms) / 1000,9:F1}");

            var outPath = Path.Combine(resultsDir, $"agent-p{AgentAnswerService.PromptVersion}-{effort}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(
                results.Select(r => new
                {
                    r.Q.Id, r.Q.Expected, r.Correct, r.Q.Question, r.Answer.ToolCalls, r.Answer.Answer,
                    Cited = r.Answer.Citations.Select(c => c.Label), WebCited = r.Answer.WebCitations.Select(c => c.Url), r.Ms,
                }),
                new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Console.WriteLine($"Soru bazlı detaylar -> {outPath}");
        }
    }

    private record Result(AgentQuestion Q, AgentAnswer Answer, long Ms, bool Correct);

    private static bool IsCorrect(string expected, List<ToolCall> calls)
    {
        var kb = calls.Any(c => c.Tool == AgentTools.KnowledgeFunction);
        var web = calls.Any(c => c.Tool == AgentTools.WebFunction);
        return expected switch
        {
            "kb" => kb && !web,
            "web" => web,
            "kb_web" => kb && web,
            "none" => !kb && !web,
            _ => throw new InvalidOperationException($"Bilinmeyen beklenen değer: {expected}"),
        };
    }
}
