using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hukuk.AI.Retrieval;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Hukuk.AI.Documents;

public record ReportParty(string Name, string Role);

// Clause: belgedeki madde ("md. 5"). Quote: maddeden aynen alıntı. HasLegalBasis false: bilgi tabanında bu konuyu
// düzenleyen hüküm bulunamadı (sadece kapsam dışı belgede gösterilir, atıfsız). Invalid: emredici hükme aykırı, madde geçersiz.
// Citations: Explanation içindeki [n] numaraları (RiskReport.Citations'a karşılık gelir).
public record ReportRisk(string Clause, string Title, string Quote, string Severity, bool Invalid, bool HasLegalBasis,
    string Explanation, string Recommendation, int[] Citations);

public record RiskReport(string DocumentType, List<ReportParty> Parties, string? Warning, List<ReportRisk> Risks,
    List<KnowledgeSource> Citations);

public static class RiskSeverity
{
    public const string High = "yuksek";
    public const string Medium = "orta";
    public const string Low = "dusuk";
}

// Yüklenen belgenin risk raporu (kararlar 2026-09-30): sabit iki aşamalı pipeline, function calling yok.
//   1) Belgenin tamamı tek LLM çağrısında okunur: taraflar + riskli maddeler + her risk için bilgi tabanı arama sorgusu.
//   2) Her risk için bilgi tabanı aranır, LLM maddeyi bulunan hükümlerle karşılaştırır: hükme aykırıysa [n] atıflı
//      risk, hükümle uyumluysa rapordan çıkar, konuyu düzenleyen hüküm yoksa dayanaksız risk (bkz. Build).
public partial class RiskReportService(KnowledgeSearch search, IChatCompletionService chat, ILogger<RiskReportService> logger)
{
    // Promptlardan biri değişince artır (eval sonuç dosyaları buna bağlı).
    // v2: hükümle çelişmeyen ama kanundaki bir hakkı anmayan maddeyi "aykırı" sayıyordu (fazla çalışma maddesi,
    //     serbest zaman seçeneğini yazmadığı için).
    // v3: çıkarım belgenin kapsam içi olup olmadığını da döndürür (temiz iş sözleşmesi, tek dayanaksız risk yüzünden
    //     kapsam dışı uyarısı alıyordu).
    public const int PromptVersion = 3;

    // Rapor eval'i (2026-10-01, 7 sözleşme, prompt v1; hepsinde 11 riskin 11'i bulundu):
    //   medium/medium 1 yanlış alarm, 44 sn; medium/low 2 yanlış alarm, 29 sn; low/low 5 yanlış alarm, 20 sn.
    // Karşılaştırmada low 15 sn kazandırıyor, fark gürültü sınırında; çıkarımda low yanlış alarmı artırıyor.
    public const string ExtractReasoningEffort = "medium";
    public const string VerifyReasoningEffort = "low";

    // Canlıda varsayılanlar kullanılır; eval alternatifleri bununla karşılaştırır ("dotnet run -- rapor matris").
    // Sorguya madde alıntısı eklemek de ölçüldü: atıf doğruluğunu artırmadı, kaldırıldı.
    public record Settings(string ExtractEffort = ExtractReasoningEffort, string VerifyEffort = VerifyReasoningEffort);

    // Chat deployment'ın TPM kotası düşük: 6 paralel çağrıda 429 (bkz. AnswerEval), 3 sorunsuz.
    private const int VerifyParallelism = 3;

    public const string OutOfScopeWarning =
        "Bu belge bilgi tabanının kapsamı (Türk Borçlar Kanunu kira hükümleri, İş Kanunu) dışında görünüyor; riskler kanun atfı olmadan listelendi.";

    public record ExtractedRisk(
        [property: Description("Maddenin belgedeki numarası, örn: 'md. 7'. Numara yoksa maddenin başlığı.")] string Clause,
        [property: Description("Riskin kısa adı, en çok 8 kelime")] string Title,
        [property: Description("Riskli ifadenin belgeden AYNEN alıntısı, en çok iki cümle")] string Quote,
        [property: Description("Madde hangi taraf için neden riskli; bir-iki cümle")] string Concern,
        [property: Description("Bu konuyu düzenleyen kanun hükmünü bulmak için kanun diliyle yazılmış kısa arama sorusu")] string SearchQuery);

    public record Extraction(
        [property: Description("Belge türü, örn: 'Konut kira sözleşmesi'")] string DocumentType,
        [property: Description("Belge bir kira sözleşmesi (konut, işyeri) veya iş sözleşmesi (işveren-işçi) ise true, başka türde ise false")] bool InScope,
        List<ReportParty> Parties,
        List<ExtractedRisk> Risks);

    public record Verdict(
        [property: Description("aykiri | uygun | dayanak_yok")] string Result,
        [property: Description("yuksek | orta | dusuk")] string Severity,
        [property: Description("Madde kaynaklardaki emredici bir hükme aykırı olduğu için geçersiz mi")] bool Invalid,
        [property: Description("Maddenin neden riskli olduğu; her hukuki bilginin sonunda kaynak numarası, örn: [2]")] string Explanation,
        [property: Description("Maddenin nasıl düzeltilmesi gerektiği; tek cümle")] string Recommendation);

    // Eval setindeki sözleşmelerden türetilmiş örnek İÇERMEZ (ölçüme kopya verilmesin).
    private const string ExtractPrompt =
        """
        Sen sözleşme inceleyen bir hukuk asistanısın. Verilen belgenin türünü, taraflarını ve riskli maddelerini çıkar.
        Riskli madde: taraflardan birinin kanundan doğan hakkını sınırlayan veya ortadan kaldıran, bir tarafa tek taraflı
        yetki veren, olağan dışı ağır bir yükümlülük ya da yaptırım getiren veya kanunun izin verdiği sınırı aşan maddedir.
        Kurallar:
        - Olağan ve dengeli maddeleri (tarafların tanıtımı, sözleşmenin konusu, ödeme günü, yetkili mahkeme gibi) listeleme.
        - Bir maddenin kanunun sınırları içinde kaldığını düşünüyorsan listeleme; emin değilsen listele, sonraki aşamada
          kanun metniyle karşılaştırılacak.
        - Her riskli madde bir kayıt; aynı maddeyi iki kez yazma.
        - Belgede yazmayan bilgi ekleme. Quote alanı belgeden aynen alınmalı.
        - Taraflarda rol, belgedeki sıfattır (kiraya veren, kiracı, işveren, işçi, satıcı, alıcı gibi).
        """;

    private const string VerifyPrompt =
        """
        Sen sözleşme inceleyen bir hukuk asistanısın. Bir sözleşme maddesi, maddeyle ilgili şüphe ve numaralı kanun
        kaynakları verilecek. Maddeyi SADECE verilen kaynaklarla karşılaştır.
        Result:
        - aykiri: Kaynaklardaki bir hüküm bu maddenin konusunu düzenliyor ve madde o hükme aykırı ya da hükmün tanıdığı
          hakkı daraltıyor. Maddenin kanundaki bir hakkı ya da seçeneği hiç anmaması aykırılık değildir (kanun yine
          uygulanır); aykırılık için maddenin hükümle çelişmesi ya da hakkı açıkça kaldırması gerekir.
        - uygun: Kaynaklardaki bir hüküm bu maddenin konusunu düzenliyor ve madde hükümle uyumlu (sınırın içinde).
        - dayanak_yok: Kaynakların hiçbiri bu maddenin konusunu düzenlemiyor. Benzer kelimeler geçen ama başka bir
          sözleşme türünü ya da konuyu düzenleyen hükümler dayanak değildir.
        Kurallar:
        - Explanation'da her hukuki bilginin sonuna dayandığı kaynağın numarasını yaz, örn: [1]. Kaynaklarda olmayan
          hukuki bilgi ekleme. Result dayanak_yok ise atıf yapma, sadece şüpheyi açıkla.
        - Severity: yuksek = ilgili tarafın önemli bir hakkını ortadan kaldırıyor ya da büyük maddi sonuç doğuruyor;
          orta = hakkı sınırlıyor, sonucu sınırlı; dusuk = küçük etkili.
        - Invalid: madde, kaynaklardaki "aşamaz", "olamaz", "geçersizdir", "değiştirilemez", "vazgeçilemez" gibi
          emredici bir hükme aykırıysa true.
        - Sade Türkçe, kısa ve net.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RiskReport> GenerateAsync(string documentText, Settings? settings = null, CancellationToken cancellationToken = default)
    {
        settings ??= new Settings();
        var sw = Stopwatch.StartNew();
        var extraction = await AskAsync<Extraction>(ExtractPrompt, $"Belge:\n\n{documentText}", settings.ExtractEffort, cancellationToken);
        var extractMs = sw.ElapsedMilliseconds;

        // DbContext thread-safe değil: aramalar sırayla, LLM karşılaştırmaları paralel.
        var sources = new List<List<KnowledgeSource>>();
        foreach (var risk in extraction.Risks)
            sources.Add(await search.SearchAsync(risk.SearchQuery, cancellationToken));
        var searchMs = sw.ElapsedMilliseconds - extractMs;

        using var gate = new SemaphoreSlim(VerifyParallelism);
        var verdicts = await Task.WhenAll(extraction.Risks.Select(async (risk, i) =>
        {
            await gate.WaitAsync(cancellationToken);
            try { return await AskAsync<Verdict>(VerifyPrompt, BuildVerifyMessage(risk, sources[i]), settings.VerifyEffort, cancellationToken); }
            finally { gate.Release(); }
        }));

        var report = Build(extraction, verdicts, sources);
        logger.LogInformation("Risk raporu: {Extracted} aday, {Kept} risk ({NoBasis} dayanaksız, kapsam dışı); çıkarım {ExtractMs} ms, arama {SearchMs} ms, karşılaştırma {VerifyMs} ms",
            extraction.Risks.Count, report.Risks.Count, report.Risks.Count(r => !r.HasLegalBasis), extractMs, searchMs, sw.ElapsedMilliseconds - extractMs - searchMs);
        return report;
    }

    // Her riskin kaynakları kendi içinde [1]'den numaralı; raporda tek atıf listesi olsun diye yeniden numaralanır.
    private static RiskReport Build(Extraction extraction, Verdict[] verdicts, List<List<KnowledgeSource>> sources)
    {
        var citations = new List<KnowledgeSource>();
        var risks = new List<ReportRisk>();
        for (var i = 0; i < verdicts.Length; i++)
        {
            var (risk, verdict) = (extraction.Risks[i], verdicts[i]);
            if (verdict.Result == "uygun")
                continue;

            var cited = new List<int>();
            var explanation = CitationMarker().Replace(verdict.Explanation, m =>
            {
                // LLM'in uydurduğu numara ([9] gibi) ya da dayanaksız riskteki atıf metinden silinir.
                var source = sources[i].FirstOrDefault(s => s.Number == int.Parse(m.Groups[1].Value));
                if (source is null || verdict.Result != "aykiri")
                    return "";
                var number = citations.FindIndex(c => c.Content == source.Content) + 1;
                if (number == 0)
                {
                    citations.Add(source with { Number = citations.Count + 1 });
                    number = citations.Count;
                }
                if (!cited.Contains(number))
                    cited.Add(number);
                return (char.IsWhiteSpace(m.Value[0]) ? " " : "") + $"[{number}]";
            }).Trim();

            // Atıfsız "aykırı" kararı kanuna dayanmıyor demektir: dayanaksız risk olarak gösterilir, geçersiz denmez.
            var hasBasis = cited.Count > 0;
            risks.Add(new ReportRisk(risk.Clause, risk.Title, risk.Quote, NormalizeSeverity(verdict.Severity),
                hasBasis && verdict.Invalid, hasBasis, explanation, verdict.Recommendation, [.. cited]));
        }

        // Karar 2026-10-01: belge kapsam içiyse (çıkarım aşaması kira/iş sözleşmesi dedi ya da en az bir risk kanuna
        // dayanıyor) dayanaksız riskler (eval'de hep yanlış alarm: gizlilik, yetkili mahkeme) rapordan çıkar.
        // Kapsam dışı belgede riskler uyarıyla ve atıfsız gösterilir.
        if (extraction.InScope || risks.Any(r => r.HasLegalBasis))
            risks.RemoveAll(r => !r.HasLegalBasis);
        var warning = risks.Count > 0 && risks.All(r => !r.HasLegalBasis) ? OutOfScopeWarning : null;
        return new RiskReport(extraction.DocumentType, extraction.Parties, warning,
            [.. risks.OrderBy(r => SeverityOrder(r.Severity))], citations);
    }

    private static string BuildVerifyMessage(ExtractedRisk risk, List<KnowledgeSource> sources)
    {
        var sb = new System.Text.StringBuilder("Kaynaklar:\n\n");
        foreach (var s in sources)
            sb.Append($"[{s.Number}] {s.Label}\n{s.Content}\n\n");
        sb.Append($"Sözleşme maddesi ({risk.Clause}): {risk.Quote}\n\nŞüphe: {risk.Concern}");
        return sb.ToString();
    }

    private async Task<T> AskAsync<T>(string systemPrompt, string userMessage, string reasoningEffort, CancellationToken cancellationToken)
    {
        var history = new ChatHistory(systemPrompt);
        history.AddUserMessage(userMessage);
        var settings = new OpenAIPromptExecutionSettings { ReasoningEffort = reasoningEffort, ResponseFormat = typeof(T) };
        var reply = await chat.GetChatMessageContentAsync(history, settings, cancellationToken: cancellationToken);
        return JsonSerializer.Deserialize<T>(reply.Content ?? "", JsonOptions) ?? throw new InvalidOperationException("Boş yanıt.");
    }

    private static string NormalizeSeverity(string severity) =>
        severity is RiskSeverity.High or RiskSeverity.Medium or RiskSeverity.Low ? severity : RiskSeverity.Medium;

    private static int SeverityOrder(string severity) => severity switch
    {
        RiskSeverity.High => 0,
        RiskSeverity.Medium => 1,
        _ => 2,
    };

    [GeneratedRegex(@"\s?\[(\d+)\]")]
    private static partial Regex CitationMarker();
}
