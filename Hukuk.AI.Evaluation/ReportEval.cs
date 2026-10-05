using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hukuk.AI.Data.Entities;
using Hukuk.AI.Documents;

namespace Hukuk.AI.Evaluation;

// data/eval/contracts-v{N}.json
public record ContractSet(int Version, List<EvalContract> Contracts);

// Risks'te olmayan maddeler temizdir. InScope false: bilgi tabanı dışı belge (kanun atfı beklenmez).
public record EvalContract(string Id, string File, string Type, bool InScope, List<ExpectedRisk> Risks);

public record ExpectedRisk(int Clause, string Severity, bool Invalid, int[] ExpectedArticles, string Risk);

// "dotnet run -- rapor": risk raporunun kalitesi. Her sözleşme canlıdaki RiskReportService'ten geçer (OCR yok,
// Markdown doğrudan verilir) ve rapor cevap anahtarıyla madde numarası üzerinden karşılaştırılır.
// "-- rapor matris": canlı ayar + alternatif reasoning seviyeleri (çıkarım/karşılaştırma).
public static partial class ReportEval
{
    private static readonly (string Name, RiskReportService.Settings Settings)[] Matrix =
    [
        ("canli (medium/low)", new()),
        ("medium/medium", new(VerifyEffort: "medium")),
        ("low/low", new(ExtractEffort: "low")),
    ];

    public static async Task RunAsync(RiskReportService reports, string setPath, string resultsDir, bool matrix)
    {
        var set = JsonSerializer.Deserialize<ContractSet>(File.ReadAllText(setPath),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
            ?? throw new InvalidOperationException($"Sözleşme seti okunamadı: {setPath}");
        var contractsDir = Path.Combine(Path.GetDirectoryName(setPath)!, Path.GetFileNameWithoutExtension(setPath));
        Directory.CreateDirectory(resultsDir);

        foreach (var (name, settings) in matrix ? Matrix : Matrix[..1])
        {
            Console.WriteLine($"\n#### {name}");
            var results = new List<(EvalContract Contract, RiskReport Report, long Ms)>();
            foreach (var contract in set.Contracts) // sırayla: servis kendi içinde zaten 3 paralel LLM çağrısı yapıyor
            {
                var text = File.ReadAllText(Path.Combine(contractsDir, contract.File));
                // 429'da beklenen süre rapor süresine sayılmasın: süre başarılı denemeden ölçülür.
                var (report, ms) = await AnswerEval.WithRetryAsync(async () =>
                {
                    var sw = Stopwatch.StartNew();
                    var r = await reports.GenerateAsync(text, settings);
                    return (r, sw.ElapsedMilliseconds);
                });
                results.Add((contract, report, ms));
                Console.WriteLine($"  {contract.Id}: {report.Risks.Count} risk, {ms / 1000.0:F1} sn");
            }

            PrintSummary(results);

            var outPath = Path.Combine(resultsDir, $"report-p{RiskReportService.PromptVersion}-{Slug().Replace(name, "-").Trim('-')}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(
                results.Select(r => new { r.Contract.Id, r.Contract.InScope, Expected = r.Contract.Risks, r.Ms, r.Report }),
                new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Console.WriteLine($"Sözleşme bazlı detaylar -> {outPath}");
        }
    }

    private static void PrintSummary(List<(EvalContract Contract, RiskReport Report, long Ms)> results)
    {
        Console.WriteLine($"\n== Risk raporu (prompt v{RiskReportService.PromptVersion}) ==");
        Console.WriteLine("Bulunan: beklenen risklerden rapora giren. Atıf: bulunanlardan beklenen kanun maddesine atıf yapan.");
        Console.WriteLine("Geçersiz / Şiddet: bulunanlardan bayrağı / şiddeti anahtarla aynı olan. Yanlış alarm: temiz maddeye yazılan risk");
        Console.WriteLine("(dayanaksız: kanun atfı olmadan yazılan).");
        Console.WriteLine($"{"Sözleşme",-10}{"Bulunan",9}{"Atıf",7}{"Geçersiz",10}{"Şiddet",8}{"Yanlış alarm",14}{"dayanaksız",12}{"sn",7}");

        int expected = 0, found = 0, cited = 0, invalid = 0, severity = 0, falseAlarms = 0, falseNoBasis = 0;
        foreach (var (contract, report, ms) in results.Where(r => r.Contract.InScope))
        {
            int cFound = 0, cCited = 0, cInvalid = 0, cSeverity = 0;
            foreach (var e in contract.Risks)
            {
                if (report.Risks.FirstOrDefault(r => ClauseNo(r.Clause) == e.Clause) is not { } risk)
                    continue;
                cFound++;
                var articles = risk.Citations.SelectMany(n => report.Citations[n - 1].Articles).ToHashSet();
                if (e.ExpectedArticles.All(n => articles.Contains(new ArticleRef(ArticleType.Asil, n))))
                    cCited++;
                if (risk.Invalid == e.Invalid)
                    cInvalid++;
                if (risk.Severity == e.Severity)
                    cSeverity++;
            }
            var alarms = report.Risks.Where(r => contract.Risks.All(e => e.Clause != ClauseNo(r.Clause))).ToList();

            Console.WriteLine($"{contract.Id,-10}{$"{cFound}/{contract.Risks.Count}",9}{cCited,7}{cInvalid,10}{cSeverity,8}" +
                              $"{alarms.Count,14}{alarms.Count(a => !a.HasLegalBasis),12}{ms / 1000.0,7:F1}");
            foreach (var a in alarms)
                Console.WriteLine($"    yanlış alarm: {a.Clause} {a.Title}{(a.HasLegalBasis ? "" : " (dayanaksız)")}");
            foreach (var e in contract.Risks.Where(e => report.Risks.All(r => ClauseNo(r.Clause) != e.Clause)))
                Console.WriteLine($"    kaçan: md. {e.Clause} (beklenen {string.Join(", ", e.ExpectedArticles)})");

            expected += contract.Risks.Count; found += cFound; cited += cCited; invalid += cInvalid; severity += cSeverity;
            falseAlarms += alarms.Count; falseNoBasis += alarms.Count(a => !a.HasLegalBasis);
        }

        double Ratio(int n, int of) => of == 0 ? 0 : (double)n / of;
        Console.WriteLine($"{"TOPLAM",-10}{$"{found}/{expected}",9}{cited,7}{invalid,10}{severity,8}{falseAlarms,14}{falseNoBasis,12}" +
                          $"{results.Average(r => r.Ms) / 1000,7:F1}");
        Console.WriteLine($"\nRisk recall {Ratio(found, expected):F3}   doğru atıf {Ratio(cited, found):F3}   geçersiz bayrağı {Ratio(invalid, found):F3}   " +
                          $"şiddet {Ratio(severity, found):F3}   yanlış alarm {falseAlarms} (kanun atıflı {falseAlarms - falseNoBasis})");

        foreach (var (contract, report, ms) in results.Where(r => !r.Contract.InScope))
        {
            var ok = report.Citations.Count == 0 && report.Risks.All(r => !r.HasLegalBasis)
                     && (report.Risks.Count == 0 || report.Warning == RiskReportService.OutOfScopeWarning);
            Console.WriteLine($"Kapsam dışı {contract.Id}: {(ok ? "DOĞRU" : "YANLIŞ")} ({report.Risks.Count} risk, {report.Citations.Count} kanun atfı, " +
                              $"uyarı {(report.Warning is null ? "yok" : "var")}, {ms / 1000.0:F1} sn)");
        }
    }

    // "md. 5", "MADDE 5 - DEPOZİTO", "Madde 5" -> 5; numara yoksa -1 (hiçbir beklenen maddeyle eşleşmez).
    private static int ClauseNo(string clause) => Number().Match(clause) is { Success: true } m ? int.Parse(m.Value) : -1;

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex Slug();
}
