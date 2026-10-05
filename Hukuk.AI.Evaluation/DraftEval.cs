using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hukuk.AI.Drafting;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Hukuk.AI.Evaluation;

// data/eval/draft-requests-v{N}.json
public record DraftRequestSet(int Version, List<EvalDraftRequest> Requests);

// Type: beklenen tür (kira | is | kapsam_disi). ExpectedMissing: soru olarak dönmesi beklenen zorunlu alanlar.
public record EvalDraftRequest(string Id, string Type, string Request, List<string> ExpectedMissing,
    Dictionary<string, string> Answers, List<PlantedRequest> Planted, List<string> Keep);

// Kullanıcının kanuna aykırı isteği: son taslakta kalmamalı, ilgili madde Expected ile uyumlu olmalı.
public record PlantedRequest(string Id, int[] Articles, string Request, string Expected);

// "dotnet run -- taslak": sözleşme taslağının kalitesi. Her istek canlıdaki akıştan geçer (bilgi çıkarımı → eksikler
// cevaplanır → yazım + denetim + düzeltme); son taslak LLM hakemle cevap anahtarına göre değerlendirilir.
public static partial class DraftEval
{
    public record PlantedVerdict(
        [property: Description("Aykırı isteğin id'si")] string Id,
        [property: Description("Taslağın HİÇBİR maddesinde bu aykırı istek yer almıyorsa VE konuyu düzenleyen madde beklenen kurala uygunsa true")] bool Fixed,
        [property: Description("Fixed false ise aykırılığın kaldığı madde ve neden; true ise boş")] string Note);

    public record KeepVerdict(
        [property: Description("Korunacak bilginin numarası")] int No,
        [property: Description("Bilgi taslakta aynı değerlerle yer alıyorsa true")] bool Present);

    public record RevisionVerdict(
        [property: Description("Düzeltmenin listedeki numarası")] int No,
        [property: Description("Düzeltmenin giderdiği aykırı isteğin id'si; hiçbiriyle ilgili değilse boş")] string PlantedId);

    public record Judgement(List<PlantedVerdict> Planted, List<KeepVerdict> Keep, List<RevisionVerdict> Revisions);

    private const string JudgePrompt =
        """
        Sen sözleşme taslağı üreten bir sistemi değerlendiren hakemsin. Bir sözleşme taslağı, kullanıcının kanuna aykırı
        istekleri (her biri için kanunun beklediği kural), taslakta korunması gereken bilgiler ve sistemin yaptığı
        düzeltmeler verilecek.
        - Planted: her aykırı istek için bir kayıt. Taslağın TAMAMINI tara: istek tek bir maddede bile (özel hükümler
          dahil) yazılı kalmışsa ya da konuyu düzenleyen madde beklenen kurala aykırıysa Fixed false. Taslak isteği
          hiç yazmamış ama konuyu beklenen kurala uygun düzenlemişse (ya da konuyu kanuna bırakmışsa) Fixed true.
        - Keep: her bilgi için bir kayıt. Bilgi taslakta aynı kişi, tutar, süre ve tarihle yer alıyorsa Present true;
          eksik, değişmiş ya da muğlak bırakılmışsa false.
        - Revisions: her düzeltme için bir kayıt. Düzeltme listedeki bir aykırı isteği gideriyorsa onun id'si; hiçbir
          aykırı istekle ilgili değilse boş.
        Sadece verilen metinlere bak; kendi hukuk bilginle yeni aykırılık arama.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private record Result(EvalDraftRequest Request, string? Type, Dictionary<string, string> Extracted, List<string> Missing, DraftResult? Draft, bool Structure,
        long FirstTokenMs, long TotalMs, Judgement? Judgement);

    private static readonly string[] IntakeEfforts = ["minimal", "low", "medium"];
    private const int IntakeRepeats = 3;
    private const int IntakeParallelism = 3; // chat deployment 6 paralelde 429 veriyor (bkz. AnswerEval)

    // "dotnet run -- taslak cikarim": sadece bilgi çıkarımı; her istek her reasoning seviyesinde IntakeRepeats kez
    // (aynı istekte koşudan koşuya değişen sonuç görülsün). Kaçan: istekte verilen ama çıkarılmayan zorunlu alan
    // (kullanıcıya gereksiz soru). Uydurulan: istekte olmayan ama dolu sayılan zorunlu alan (sorulmadan taslağa girer).
    public static async Task RunIntakeAsync(DraftIntake intake, string setPath, string resultsDir)
    {
        var set = JsonSerializer.Deserialize<DraftRequestSet>(File.ReadAllText(setPath),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
            ?? throw new InvalidOperationException($"Taslak seti okunamadı: {setPath}");
        Directory.CreateDirectory(resultsDir);

        var details = new List<object>();
        Console.WriteLine($"\n== Bilgi çıkarımı (prompt v{DraftIntake.PromptVersion}, {set.Requests.Count} istek × {IntakeRepeats} tekrar) ==");
        Console.WriteLine($"{"Seviye",-9}{"Tür",8}{"Eksik tam",11}{"Kaçan",7}{"Uydurulan",11}{"sn",6}");
        foreach (var effort in IntakeEfforts)
        {
            using var gate = new SemaphoreSlim(IntakeParallelism);
            var runs = await Task.WhenAll(set.Requests.SelectMany(r => Enumerable.Range(1, IntakeRepeats).Select(async run =>
            {
                await gate.WaitAsync();
                try
                {
                    var ((template, fields), ms) = await AnswerEval.WithRetryAsync(async () =>
                    {
                        var sw = Stopwatch.StartNew();
                        var result = await intake.ExtractAsync(r.Request, effort: effort);
                        return (result, sw.ElapsedMilliseconds);
                    });
                    return (Request: r, Run: run, Type: template?.Type ?? DraftIntake.OutOfScope, Fields: fields,
                        Missing: template?.Missing(fields).Select(f => f.Key).ToList() ?? [], Ms: ms);
                }
                finally { gate.Release(); }
            })));

            var inScope = runs.Where(x => x.Request.Type != DraftIntake.OutOfScope).ToList();
            int exact = 0, missed = 0, invented = 0;
            var lines = new List<string>();
            foreach (var x in inScope)
            {
                // Tür yanlışsa alanlar karşılaştırılamaz: hepsi kaçmış sayılır.
                var missedKeys = x.Type == x.Request.Type ? x.Missing.Except(x.Request.ExpectedMissing).ToList() : ["(tür yanlış)"];
                var inventedKeys = x.Type == x.Request.Type ? x.Request.ExpectedMissing.Except(x.Missing).ToList() : [];
                exact += missedKeys.Count == 0 && inventedKeys.Count == 0 ? 1 : 0;
                missed += missedKeys.Count;
                invented += inventedKeys.Count;
                if (missedKeys.Count > 0)
                    lines.Add($"    {x.Request.Id} #{x.Run} kaçan: {string.Join(", ", missedKeys)}");
                foreach (var key in inventedKeys)
                    lines.Add($"    {x.Request.Id} #{x.Run} uydurulan: {key} = \"{x.Fields[key]}\"");
            }
            Console.WriteLine($"{effort,-9}{$"{runs.Count(x => x.Type == x.Request.Type)}/{runs.Length}",8}{$"{exact}/{inScope.Count}",11}{missed,7}{invented,11}{runs.Average(x => x.Ms) / 1000,6:F1}");
            lines.ForEach(Console.WriteLine);
            details.AddRange(runs.Select(x => new { Effort = effort, x.Request.Id, x.Run, ExpectedType = x.Request.Type, x.Type, x.Request.ExpectedMissing, x.Missing, x.Fields, x.Ms }));
        }

        var outPath = Path.Combine(resultsDir, $"draft-intake-p{DraftIntake.PromptVersion}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(details, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine($"Koşu bazlı detaylar -> {outPath}");
    }

    public static async Task RunAsync(DraftIntake intake, DraftWriter writer, IChatCompletionService chat, string setPath, string resultsDir)
    {
        var set = JsonSerializer.Deserialize<DraftRequestSet>(File.ReadAllText(setPath),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
            ?? throw new InvalidOperationException($"Taslak seti okunamadı: {setPath}");
        Directory.CreateDirectory(resultsDir);

        var results = new List<Result>();
        foreach (var request in set.Requests) // sırayla: denetim kendi içinde zaten 3 paralel LLM çağrısı yapıyor
        {
            var (template, fields) = await AnswerEval.WithRetryAsync(() => intake.ExtractAsync(request.Request));
            var missing = template?.Missing(fields).Select(f => f.Key).ToList() ?? [];
            var extracted = new Dictionary<string, string>(fields); // cevaplar eklenmeden önceki hali
            if (template is null)
            {
                results.Add(new Result(request, null, extracted, missing, null, false, 0, 0, null));
                Console.WriteLine($"  {request.Id}: kapsam dışı");
                continue;
            }

            foreach (var (key, value) in DraftIntake.Clean(template, request.Answers))
                fields[key] = value;
            if (template.Missing(fields).Count > 0) // cevap anahtarı yetmedi: çıkarım verilen bir bilgiyi kaçırdı
            {
                results.Add(new Result(request, template.Type, extracted, missing, null, false, 0, 0, null));
                Console.WriteLine($"  {request.Id}: eksik bilgi kaldı ({string.Join(", ", template.Missing(fields).Select(f => f.Key))}), taslak yazılmadı");
                continue;
            }

            // 429'da beklenen süre sayılmasın: süreler başarılı denemeden ölçülür.
            var (draft, firstTokenMs, totalMs) = await AnswerEval.WithRetryAsync(async () =>
            {
                var sw = Stopwatch.StartNew();
                long first = 0;
                var events = new DraftEvents(_ => { if (first == 0) first = sw.ElapsedMilliseconds; return Task.CompletedTask; },
                    () => Task.CompletedTask, _ => Task.CompletedTask);
                var d = await writer.WriteAsync(template, fields, events);
                return (d, first, sw.ElapsedMilliseconds);
            });

            // Yapı: maddeler iskeletteki bölümlerle aynı başlık ve sırada mı. Koşullu bölüm (Özel Hükümler) olmayabilir:
            // bütün özel istekler kendi bölümüne yazıldıysa atlanır.
            var structure = ClauseHeading().Matches(draft.Draft).Select(m => m.Groups[1].Value)
                .Where(title => template.Sections.All(s => s.OnlyIfField is null || !string.Equals(s.Title, title, StringComparison.CurrentCultureIgnoreCase)))
                .SequenceEqual(template.Sections.Where(s => s.OnlyIfField is null).Select(s => s.Title), StringComparer.CurrentCultureIgnoreCase);

            var judgement = await AnswerEval.WithRetryAsync(() => JudgeAsync(chat, request, draft));
            results.Add(new Result(request, template.Type, extracted, missing, draft, structure, firstTokenMs, totalMs, judgement));
            Console.WriteLine($"  {request.Id}: {draft.Revisions.Count} düzeltme, ilk parça {firstTokenMs / 1000.0:F1} sn, toplam {totalMs / 1000.0:F1} sn");
        }

        PrintSummary(results);

        var outPath = Path.Combine(resultsDir, $"draft-p{DraftWriter.PromptVersion}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(
            results.Select(r => new { r.Request.Id, ExpectedType = r.Request.Type, r.Type, r.Request.ExpectedMissing, r.Missing, r.Extracted, r.Structure, r.FirstTokenMs, r.TotalMs, r.Judgement, r.Draft }),
            new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine($"İstek bazlı detaylar -> {outPath}");
    }

    private static async Task<Judgement> JudgeAsync(IChatCompletionService chat, EvalDraftRequest request, DraftResult draft)
    {
        var sb = new StringBuilder($"Taslak:\n\n{draft.Draft}\n\nAykırı istekler:\n");
        foreach (var p in request.Planted)
            sb.Append($"- id {p.Id}: istek: {p.Request} | beklenen kural: {p.Expected}\n");
        sb.Append("\nKorunacak bilgiler:\n");
        for (var i = 0; i < request.Keep.Count; i++)
            sb.Append($"{i + 1}. {request.Keep[i]}\n");
        sb.Append("\nDüzeltmeler:\n");
        for (var i = 0; i < draft.Revisions.Count; i++)
            sb.Append($"{i + 1}. {draft.Revisions[i].Clause} {draft.Revisions[i].Title}: {draft.Revisions[i].Reason}\n");

        var history = new ChatHistory(JudgePrompt);
        history.AddUserMessage(sb.ToString());
        var settings = new OpenAIPromptExecutionSettings { ReasoningEffort = "low", ResponseFormat = typeof(Judgement) };
        var reply = await chat.GetChatMessageContentAsync(history, settings);
        return JsonSerializer.Deserialize<Judgement>(reply.Content ?? "", JsonOptions) ?? throw new InvalidOperationException("Boş yanıt.");
    }

    private static void PrintSummary(List<Result> results)
    {
        Console.WriteLine($"\n== Taslak (çıkarım prompt v{DraftIntake.PromptVersion}, yazım prompt v{DraftWriter.PromptVersion}) ==");
        Console.WriteLine("Tür / Eksik: bilgi çıkarımı türü ve sorulacak alanları doğru buldu mu. Yapı: maddeler iskeletle aynı mı.");
        Console.WriteLine("Giderilen: kullanıcının aykırı isteklerinden son taslakta kalmayan. Korunan: kanuna uygun bilgilerden taslakta duran.");
        Console.WriteLine("İstek dışı: aykırı istekle ilgisi olmayan düzeltme (yanlış alarm ya da yazımın kendi hatası). Kalan: denetimin bulup düzeltilemeyen riski.");
        Console.WriteLine($"{"İstek",-7}{"Tür",5}{"Eksik",7}{"Yapı",6}{"Giderilen",11}{"Korunan",9}{"Düzeltme",10}{"İstek dışı",11}{"Kalan",7}{"İlk sn",8}{"Toplam",8}");

        int typeOk = 0, missingOk = 0, structureOk = 0, planted = 0, fixedCount = 0, keep = 0, kept = 0, revisions = 0, unneeded = 0, unresolved = 0;
        foreach (var r in results)
        {
            var isTypeOk = (r.Type ?? DraftIntake.OutOfScope) == r.Request.Type;
            var isMissingOk = isTypeOk && r.Missing.Order().SequenceEqual(r.Request.ExpectedMissing.Order());
            typeOk += isTypeOk ? 1 : 0;
            if (r.Request.Type == DraftIntake.OutOfScope)
            {
                Console.WriteLine($"{r.Request.Id,-7}{(isTypeOk ? "+" : "-"),5}");
                continue;
            }
            missingOk += isMissingOk ? 1 : 0;
            planted += r.Request.Planted.Count;
            keep += r.Request.Keep.Count;
            if (r.Draft is null || r.Judgement is null) // taslak yazılamadı: aykırılar giderilmedi, bilgiler korunmadı sayılır
            {
                Console.WriteLine($"{r.Request.Id,-7}{(isTypeOk ? "+" : "-"),5}{(isMissingOk ? "+" : "-"),7}   taslak yok (bulunan eksikler: {string.Join(", ", r.Missing)})");
                continue;
            }

            var rFixed = r.Request.Planted.Count(p => r.Judgement.Planted.Any(v => v.Id == p.Id && v.Fixed));
            var rKept = Enumerable.Range(1, r.Request.Keep.Count).Count(n => r.Judgement.Keep.Any(v => v.No == n && v.Present));
            // Hakem düzeltmeyi bir aykırı isteğe bağlamadıysa istek dışıdır (yanlış alarm ya da yazımın kendi hatası).
            var unneededRevisions = r.Draft.Revisions.Where((_, i) => r.Judgement.Revisions.FirstOrDefault(v => v.No == i + 1) is not { PlantedId.Length: > 0 }).ToList();
            var rUnneeded = unneededRevisions.Count;
            structureOk += r.Structure ? 1 : 0;
            fixedCount += rFixed; kept += rKept; revisions += r.Draft.Revisions.Count; unneeded += rUnneeded; unresolved += r.Draft.Unresolved.Count;

            Console.WriteLine($"{r.Request.Id,-7}{(isTypeOk ? "+" : "-"),5}{(isMissingOk ? "+" : "-"),7}{(r.Structure ? "+" : "-"),6}" +
                              $"{$"{rFixed}/{r.Request.Planted.Count}",11}{$"{rKept}/{r.Request.Keep.Count}",9}{r.Draft.Revisions.Count,10}{rUnneeded,11}{r.Draft.Unresolved.Count,7}" +
                              $"{r.FirstTokenMs / 1000.0,8:F1}{r.TotalMs / 1000.0,8:F1}");
            if (!isMissingOk)
                Console.WriteLine($"    eksik: bulunan [{string.Join(", ", r.Missing)}], beklenen [{string.Join(", ", r.Request.ExpectedMissing)}]");
            foreach (var v in r.Judgement.Planted.Where(v => !v.Fixed && r.Request.Planted.Any(p => p.Id == v.Id)))
                Console.WriteLine($"    giderilmedi: {v.Id}: {v.Note}");
            for (var n = 1; n <= r.Request.Keep.Count; n++)
                if (!r.Judgement.Keep.Any(v => v.No == n && v.Present))
                    Console.WriteLine($"    korunmadı: {r.Request.Keep[n - 1]}");
            foreach (var rev in unneededRevisions)
                Console.WriteLine($"    istek dışı düzeltme: {rev.Clause} {rev.Title}");
        }

        var inScope = results.Count(r => r.Request.Type != DraftIntake.OutOfScope);
        var written = results.Where(r => r.Draft is not null).ToList();
        double Ratio(int n, int of) => of == 0 ? 0 : (double)n / of;
        Console.WriteLine($"\nTür {typeOk}/{results.Count}   eksik bilgi {missingOk}/{inScope}   yapı {structureOk}/{inScope}   " +
                          $"giderilen {fixedCount}/{planted} ({Ratio(fixedCount, planted):F3})   korunan {kept}/{keep} ({Ratio(kept, keep):F3})   " +
                          $"düzeltme {revisions} (istek dışı {unneeded})   kalan risk {unresolved}");
        if (written.Count > 0)
            Console.WriteLine($"İlk parça ort. {written.Average(r => r.FirstTokenMs) / 1000:F1} sn   toplam ort. {written.Average(r => r.TotalMs) / 1000:F1} sn ({written.Count} taslak)");
    }

    [GeneratedRegex(@"^##\s*MADDE\s+\d+\s*[–—:-]\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClauseHeading();
}
