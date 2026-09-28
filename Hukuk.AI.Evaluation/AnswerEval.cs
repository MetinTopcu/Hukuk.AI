using System.ClientModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hukuk.AI.Data.Entities;
using Hukuk.AI.Retrieval;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Pgvector;

namespace Hukuk.AI.Evaluation;

// "dotnet run -- cevap": cevap kalitesi ve süresi, bütçe (2000/4000) x cevap reasoning seviyesi.
// Her soru için kaynaklar bir kez (4000 bütçeyle) aranır; 2000 bütçe aynı sıralamanın başıdır.
// LLM-hakem cevabı answer_key'e göre puanlar. Hakem de gpt-5-mini (tek chat deployment'ımız): kendi cevabını
// kayırma eğilimi olabilir, bu yüzden mutlak puandan çok ayarlar arası fark anlamlı.
public static class AnswerEval
{
    private static readonly int[] Budgets = [2000, 4000];
    private static readonly string?[] Efforts = ["minimal", "low", null]; // null: varsayılan (medium)
    // Chat deployment'ın TPM kotası düşük (6 paralelde 28. cevapta 429); her sonuç anında dosyaya yazılır,
    // yarıda kalırsa aynı komut kaldığı yerden devam eder (prompt sürümü değişince yeni dosya).
    private const int Parallelism = 3;

    public record Judgement(
        [property: Description("0: yanlış veya ana noktayı kaçırıyor, 1: kısmen doğru/eksik, 2: referanstaki ana noktaları doğru veriyor")] int Score,
        [property: Description("Cevap referansla çelişen veya kaynaksız uydurma hukuki bilgi içeriyor mu")] bool Fabricated,
        [property: Description("Tek cümle gerekçe")] string Reason);

    private const string JudgePrompt =
        """
        Bir hukuk asistanının cevabını değerlendiriyorsun. Sana soru, referans cevap ve asistanın cevabı verilecek.
        Referans cevap doğru bilgiyi veya beklenen davranışı (ör. "bilgi tabanında yok, uydurmamalı") tanımlar.
        Puan:
        - 2: Referanstaki ana hukuki noktaları doğru veriyor (ifade farklı olabilir; referansta olmayan ama doğru ek bilgi puan düşürmez).
          Referans "bilgi tabanında yok / kapsam dışı" diyorsa: asistan bu konuda bilgi veremediğini söylüyor ve konu dışı hüküm aktarmıyor.
        - 1: Kısmen doğru; önemli bir nokta eksik veya belirsiz.
        - 0: Yanlış, ana noktayı kaçırıyor ya da kapsam dışı soruda ilgisiz hükümlerle cevap vermeye çalışıyor.
        Fabricated: referansla çelişen bir hukuki iddia (yanlış süre, oran, koşul) varsa true.
        """;

    public static async Task RunAsync(
        LegalAnswerService answers, KnowledgeSearch search, IChatCompletionService chat,
        List<(EvalQuestion q, Vector combined)> questions, string resultsDir)
    {
        Console.WriteLine($"Kaynaklar aranıyor ({questions.Count} soru)...");
        var sources = new Dictionary<string, List<KnowledgeSource>>();
        foreach (var (q, v) in questions) // DbContext thread-safe değil: sırayla
            sources[q.Id] = await search.SearchAsync(q.Question, v, Budgets.Max());

        var configs = (from b in Budgets from e in Efforts select (budget: b, effort: e)).ToList();
        Directory.CreateDirectory(resultsDir);
        var progressPath = Path.Combine(resultsDir, $"answers-p{LegalAnswerService.PromptVersion}.progress.jsonl");
        var byId = questions.ToDictionary(x => x.q.Id, x => x.q);
        var results = new ConcurrentBag<Result>(File.Exists(progressPath)
            ? File.ReadLines(progressPath).Select(l => JsonSerializer.Deserialize<SavedResult>(l, JsonOptions)!)
                .Select(r => new Result(r.Budget, r.Effort, byId[r.Id], r.Answer, r.AnswerMs, r.Judgement))
            : []);
        var finished = results.Select(r => (r.Budget, r.Effort, r.Q.Id)).ToHashSet();
        if (finished.Count > 0)
            Console.WriteLine($"{progressPath}: {finished.Count} sonuç önceki çalıştırmadan alındı.");

        using var gate = new SemaphoreSlim(Parallelism);
        using var fileLock = new SemaphoreSlim(1);
        var done = finished.Count;
        var total = configs.Count * questions.Count;

        await Task.WhenAll(
            from c in configs
            from x in questions
            where !finished.Contains((c.budget, EffortName(c.effort), x.q.Id))
            select Task.Run(async () =>
            {
                await gate.WaitAsync();
                try
                {
                    var context = KnowledgeSearch.WithinBudget(sources[x.q.Id], c.budget);
                    // 429'da beklenen süre cevap süresine sayılmasın: süre başarılı denemeden ölçülür.
                    var (answer, ms) = await WithRetryAsync(async () =>
                    {
                        var sw = Stopwatch.StartNew();
                        var a = await answers.GenerateAsync(x.q.Question, context, c.effort);
                        return (a, sw.ElapsedMilliseconds);
                    });
                    var judgement = await WithRetryAsync(() => JudgeAsync(chat, x.q, answer.Answer));
                    var result = new Result(c.budget, EffortName(c.effort), x.q, answer, ms, judgement);
                    results.Add(result);

                    await fileLock.WaitAsync();
                    try { File.AppendAllText(progressPath, JsonSerializer.Serialize(new SavedResult(result.Budget, result.Effort, x.q.Id, answer, ms, judgement), JsonOptions) + Environment.NewLine); }
                    finally { fileLock.Release(); }
                    Console.Write($"\r  {Interlocked.Increment(ref done)}/{total}");
                }
                finally { gate.Release(); }
            }));
        Console.WriteLine();

        PrintSummary(results.ToList(), configs);
        PrintTopSimilarity(questions, sources);

        var outPath = Path.Combine(resultsDir, $"answers-p{LegalAnswerService.PromptVersion}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(
            results.OrderBy(r => r.Q.Id).ThenBy(r => r.Budget).ThenBy(r => r.Effort).Select(r => new
            {
                r.Budget, r.Effort, r.Q.Id, r.Q.Type, r.Q.Question, r.Q.AnswerKey, r.Answer.Answer,
                Cited = r.Answer.Citations.Select(c => c.Label), r.AnswerMs, r.Judgement,
            }),
            new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Converters = { new JsonStringEnumConverter() } }));
        Console.WriteLine($"\nSoru bazlı detaylar -> {outPath}");
    }

    private record Result(int Budget, string Effort, EvalQuestion Q, LegalAnswer Answer, long AnswerMs, Judgement Judgement);

    private record SavedResult(int Budget, string Effort, string Id, LegalAnswer Answer, long AnswerMs, Judgement Judgement);

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // SDK'nın kendi retry'ı birkaç saniye bekler; TPM kotası dolunca (429) ~1 dk beklemek gerekir.
    // SK, SDK hatasını HttpOperationException içine sarar.
    private static async Task<T> WithRetryAsync<T>(Func<Task<T>> action, int maxAttempts = 8)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (HttpOperationException ex) when (ex.InnerException is ClientResultException { Status: 429 } inner && attempt < maxAttempts)
            {
                var wait = inner.GetRawResponse()?.Headers.TryGetValue("retry-after", out var value) == true && int.TryParse(value, out var seconds)
                    ? TimeSpan.FromSeconds(seconds + 1)
                    : TimeSpan.FromSeconds(60);
                await Task.Delay(wait);
            }
        }
    }

    private static async Task<Judgement> JudgeAsync(IChatCompletionService chat, EvalQuestion q, string answer)
    {
        var history = new ChatHistory(JudgePrompt);
        history.AddUserMessage($"Soru: {q.Question}\n\nReferans cevap: {q.AnswerKey}\n\nAsistanın cevabı:\n{answer}");
        var settings = new OpenAIPromptExecutionSettings { ReasoningEffort = "low", ResponseFormat = typeof(Judgement) };
        var reply = await chat.GetChatMessageContentAsync(history, settings);
        return JsonSerializer.Deserialize<Judgement>(reply.Content!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static void PrintSummary(List<Result> results, List<(int budget, string? effort)> configs)
    {
        Console.WriteLine($"\n== Cevap kalitesi (prompt v{LegalAnswerService.PromptVersion}) ==");
        Console.WriteLine("Puan: hakem 0-2 ortalaması / 2. Atıf: beklenen maddelerin cevapta atıf yapılan oranı. Kapsam dışı: kapsam_disi+belirsiz puanı.");
        Console.WriteLine($"{"Bütçe",-7}{"Reasoning",-11}{"Puan",7}{"Uydurma",9}{"Atıf",7}{"Kapsam dışı",13}{"Ort. sn",9}{"p90 sn",8}");

        foreach (var (budget, effort) in configs)
        {
            var rs = results.Where(r => r.Budget == budget && r.Effort == EffortName(effort)).ToList();
            var scored = rs.Where(r => r.Q.IsScored).ToList();
            var outOfScope = rs.Where(r => !r.Q.IsScored).ToList();
            var ms = rs.Select(r => r.AnswerMs).Order().ToList();

            var citation = scored.Average(r =>
            {
                var cited = r.Answer.Citations.SelectMany(c => c.Articles).ToHashSet();
                return r.Q.ExpectedArticles.Count(n => cited.Contains(new ArticleRef(ArticleType.Asil, n))) / (double)r.Q.ExpectedArticles.Length;
            });

            Console.WriteLine($"{budget,-7}{EffortName(effort),-11}{scored.Average(r => r.Judgement.Score) / 2,7:F3}" +
                              $"{rs.Average(r => r.Judgement.Fabricated ? 1.0 : 0),9:F3}{citation,7:F3}" +
                              $"{outOfScope.Average(r => r.Judgement.Score) / 2,13:F3}" +
                              $"{ms.Average() / 1000,9:F1}{ms[(int)(ms.Count * 0.9)] / 1000.0,8:F1}");
        }
    }

    // Kapsam dışı tespiti için: Birlesik sorguyla en iyi kaynağın benzerliği soru türüne göre ayrışıyor mu?
    // Madde atfıyla doğrudan gelen kaynak benzerliği 1 olduğundan vektör sonucu kullanılır (Similarity < 1).
    private static void PrintTopSimilarity(List<(EvalQuestion q, Vector combined)> questions, Dictionary<string, List<KnowledgeSource>> sources)
    {
        Console.WriteLine("\n== En iyi kaynağın benzerliği (Birlesik sorgu), türe göre min / ort / maks ==");
        foreach (var g in questions.GroupBy(x => x.q.Type))
        {
            var top = g.Select(x => sources[x.q.Id].Where(s => s.Similarity < 1).Select(s => s.Similarity).DefaultIfEmpty(0).Max()).ToList();
            Console.WriteLine($"{g.Key,-12}{top.Min(),8:F3}{top.Average(),8:F3}{top.Max(),8:F3}   (n={top.Count})");
        }
    }

    // "dotnet run -- sure": aramanın canlı aşamaları (cevap üretimi hariç), ilk n soru, sırayla.
    public static async Task MeasureSearchLatencyAsync(QueryRewriter rewriter, Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>> generator,
        List<EvalQuestion> questions, int n = 10)
    {
        Console.WriteLine($"\n== Arama aşamalarının süresi (ilk {n} soru), ort / maks sn ==");
        foreach (var effort in new[] { "minimal", "low" })
        {
            var rewriteMs = new List<long>();
            var embedMs = new List<long>();
            foreach (var q in questions.Take(n))
            {
                var sw = Stopwatch.StartNew();
                var rewrite = await rewriter.RewriteAsync(q.Question, effort);
                rewriteMs.Add(sw.ElapsedMilliseconds);
                sw.Restart();
                await Microsoft.Extensions.AI.EmbeddingGeneratorExtensions.GenerateVectorAsync(generator, QueryRewriter.Combine(q.Question, rewrite));
                embedMs.Add(sw.ElapsedMilliseconds);
            }
            Console.WriteLine($"Yeniden yazım ({effort,-7}) {rewriteMs.Average() / 1000,6:F1} / {rewriteMs.Max() / 1000.0,5:F1}   " +
                              $"embedding {embedMs.Average() / 1000,5:F1} / {embedMs.Max() / 1000.0,5:F1}");
        }
    }

    private static string EffortName(string? effort) => effort ?? "medium";
}
