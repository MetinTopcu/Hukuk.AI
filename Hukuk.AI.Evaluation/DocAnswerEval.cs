using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hukuk.AI.Data.Entities;
using Hukuk.AI.Documents;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Hukuk.AI.Evaluation;

// data/eval/doc-questions-v{N}.json
public record DocQuestionSet(int Version, List<DocQuestion> Questions);

// ExpectedClauses: atıf beklenen belge maddeleri. ExpectedArticles: atıf beklenen kanun maddeleri (boşsa kanun atfı gereksiz).
public record DocQuestion(string Id, string Contract, string Type, string Question, int[] ExpectedClauses, int[] ExpectedArticles, string AnswerKey);

// "dotnet run -- belgesoru": yüklenen belge üzerinde soru-cevabın kalitesi. Sözleşmeler canlıdaki gibi parçalanıp
// Redis'e yazılır (OCR yok, Markdown doğrudan), sorular canlıdaki DocumentAnswerService'ten geçer, LLM-hakem cevabı
// answer_key'e göre puanlar (hakem de gpt-5-mini: mutlak puandan çok ayarlar arası fark anlamlı).
// "-- belgesoru matris": canlı ayar + alternatif reasoning seviyeleri.
public static partial class DocAnswerEval
{
    private static readonly (string Name, DocumentAnswerService.Settings Settings)[] Matrix =
    [
        ("canli (minimal)", new()),
        ("low", new(AnswerEffort: "low")),
        ("medium", new(AnswerEffort: "medium")),
        // Karar 2026-10-01: kanun araması "soru + en yakın belge parçası" ile; bu satır sadece soruyla aramayı ölçer.
        ("sadece-soru (minimal)", new(QueryWithChunk: false)),
    ];

    private const string JudgePrompt =
        """
        Bir hukuk asistanının, kullanıcının yüklediği sözleşme hakkındaki bir soruya verdiği cevabı değerlendiriyorsun.
        Sana soru, referans cevap ve asistanın cevabı verilecek. Referans cevap doğru bilgiyi veya beklenen davranışı
        (ör. "sözleşmede yok, bilgi veremediğini söylemeli") tanımlar.
        Puan:
        - 2: Referanstaki ana noktaları doğru veriyor: sözleşmede ne yazdığı ve (referansta varsa) kanuna uygun olup
          olmadığı sonucu doğru (ifade farklı olabilir; referansta olmayan ama doğru ek bilgi puan düşürmez).
          Referans "sözleşmede/kaynaklarda yok" diyorsa: asistan bilgi veremediğini söylüyor ve ilgisiz hüküm aktarmıyor.
        - 1: Kısmen doğru; önemli bir nokta eksik, belirsiz ya da sonuç net değil.
        - 0: Yanlış, sonucu ters veriyor, ana noktayı kaçırıyor ya da olmayan bilgiyi varmış gibi anlatıyor.
        Fabricated: referansla çelişen bir iddia (yanlış tutar, süre, oran, koşul) varsa true.
        """;

    public static async Task RunAsync(DocumentAnswerService answers, DocumentStore store, DocumentChunker chunker,
        IEmbeddingGenerator<string, Embedding<float>> embeddings, IChatCompletionService chat,
        string setPath, string contractsDir, string resultsDir, bool matrix)
    {
        var set = JsonSerializer.Deserialize<DocQuestionSet>(File.ReadAllText(setPath),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
            ?? throw new InvalidOperationException($"Soru seti okunamadı: {setPath}");
        Directory.CreateDirectory(resultsDir);

        // Sözleşmeler geçici belge olarak Redis'e yazılır (2 saat sonra kendiliğinden silinir).
        await store.EnsureIndexAsync();
        var documents = new Dictionary<string, (string Id, int Chunks)>();
        foreach (var contract in set.Questions.Select(q => q.Contract).Distinct())
        {
            var file = Directory.GetFiles(contractsDir, $"{contract}-*.md").Single();
            var chunks = chunker.Split(Path.GetFileName(file), File.ReadAllText(file));
            var vectors = (await embeddings.GenerateAsync(chunks.Select(c => c.Content))).Select(e => e.Vector.ToArray()).ToList();
            var id = Guid.NewGuid().ToString("N");
            await store.SaveChunksAsync(id, 1, chunks, vectors);
            documents[contract] = (id, chunks.Count);
        }

        foreach (var (name, settings) in matrix ? Matrix : Matrix[..1])
        {
            Console.WriteLine($"\n#### {name}");
            var results = new List<Result>();
            foreach (var q in set.Questions) // sırayla: bilgi tabanı araması DbContext kullanıyor, TPM kotası düşük
            {
                var (id, chunkCount) = documents[q.Contract];
                // 429'da beklenen süre cevap süresine sayılmasın: süre başarılı denemeden ölçülür.
                var (answer, ms) = await AnswerEval.WithRetryAsync(async () =>
                {
                    var sw = Stopwatch.StartNew();
                    var a = await answers.AnswerAsync(id, chunkCount, q.Question, answerSettings: settings);
                    return (a, sw.ElapsedMilliseconds);
                });
                var judgement = await AnswerEval.WithRetryAsync(() => JudgeAsync(chat, q, answer.Answer));
                results.Add(new Result(q, answer, ms, judgement));
                Console.Write($"\r  {results.Count}/{set.Questions.Count}");
            }
            Console.WriteLine();

            PrintSummary(results);

            var outPath = Path.Combine(resultsDir, $"docanswers-p{DocumentAnswerService.PromptVersion}-{Slug().Replace(name, "-").Trim('-')}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(
                results.Select(r => new
                {
                    r.Q.Id, r.Q.Contract, r.Q.Type, r.Q.Question, r.Q.AnswerKey, r.Answer.Answer,
                    DocumentCited = r.Answer.DocumentCitations.Select(c => c.Label), r.Q.ExpectedClauses,
                    Cited = r.Answer.Citations.Select(c => c.Label), r.Q.ExpectedArticles,
                    Retrieved = r.Answer.KnowledgeContext.Select(c => c.Label), r.Ms, r.Judgement,
                }),
                new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Console.WriteLine($"Soru bazlı detaylar -> {outPath}");
        }
    }

    private record Result(DocQuestion Q, DocumentAnswer Answer, long Ms, AnswerEval.Judgement Judgement);

    private static async Task<AnswerEval.Judgement> JudgeAsync(IChatCompletionService chat, DocQuestion q, string answer)
    {
        var history = new ChatHistory(JudgePrompt);
        history.AddUserMessage($"Soru: {q.Question}\n\nReferans cevap: {q.AnswerKey}\n\nAsistanın cevabı:\n{answer}");
        var settings = new OpenAIPromptExecutionSettings { ReasoningEffort = "low", ResponseFormat = typeof(AnswerEval.Judgement) };
        var reply = await chat.GetChatMessageContentAsync(history, settings);
        return JsonSerializer.Deserialize<AnswerEval.Judgement>(reply.Content!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static void PrintSummary(List<Result> results)
    {
        Console.WriteLine($"\n== Belge soru-cevap (prompt v{DocumentAnswerService.PromptVersion}) ==");
        Console.WriteLine("Puan: hakem 0-2 ortalaması / 2. Belge atfı / Kanun atfı: beklenen maddelerin cevapta atıf yapılan oranı.");
        Console.WriteLine("Gereksiz kanun atfı: kanun atfı beklenmeyen sorulardan, cevabı kanun atfı içerenler.");
        Console.WriteLine("Arama: beklenen kanun maddelerinin LLM'e verilen kaynaklar içinde bulunma oranı (kanun atfının üst sınırı).");
        Console.WriteLine($"{"Tür",-24}{"n",4}{"Puan",8}{"Uydurma",9}{"Belge atfı",12}{"Arama",8}{"Kanun atfı",12}{"Gereksiz",10}{"Ort. sn",9}");

        foreach (var g in results.GroupBy(r => r.Q.Type).Select(g => (g.Key, g.ToList())).Append(("TOPLAM", results)))
        {
            var (type, rs) = g;
            var withClauses = rs.Where(r => r.Q.ExpectedClauses.Length > 0).ToList();
            var withArticles = rs.Where(r => r.Q.ExpectedArticles.Length > 0).ToList();
            var withoutArticles = rs.Where(r => r.Q.ExpectedArticles.Length == 0).ToList();

            var clause = withClauses.Count == 0 ? "-" : withClauses.Average(r =>
            {
                var cited = r.Answer.DocumentCitations.Select(c => ClauseNo(c.Label)).ToHashSet();
                return r.Q.ExpectedClauses.Count(cited.Contains) / (double)r.Q.ExpectedClauses.Length;
            }).ToString("F3");
            string Articles(Func<Result, List<Hukuk.AI.Retrieval.KnowledgeSource>> sources) => withArticles.Count == 0 ? "-" : withArticles.Average(r =>
            {
                var found = sources(r).SelectMany(c => c.Articles).ToHashSet();
                return r.Q.ExpectedArticles.Count(n => found.Contains(new ArticleRef(ArticleType.Asil, n))) / (double)r.Q.ExpectedArticles.Length;
            }).ToString("F3");
            var (retrieved, article) = (Articles(r => r.Answer.KnowledgeContext), Articles(r => r.Answer.Citations));
            var unnecessary = withoutArticles.Count == 0 ? "-" : $"{withoutArticles.Count(r => r.Answer.Citations.Count > 0)}/{withoutArticles.Count}";

            Console.WriteLine($"{type,-24}{rs.Count,4}{rs.Average(r => r.Judgement.Score) / 2,8:F3}{rs.Count(r => r.Judgement.Fabricated),9}" +
                              $"{clause,12}{retrieved,8}{article,12}{unnecessary,10}{rs.Average(r => r.Ms) / 1000.0,9:F1}");
        }

        foreach (var r in results.Where(r => r.Judgement.Score < 2))
            Console.WriteLine($"    {r.Q.Id} ({r.Q.Type}) puan {r.Judgement.Score}: {r.Judgement.Reason}");
    }

    // "md. 5" -> 5; numara yoksa -1 (hiçbir beklenen maddeyle eşleşmez).
    private static int ClauseNo(string label) => Number().Match(label) is { Success: true } m ? int.Parse(m.Value) : -1;

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex Slug();
}
