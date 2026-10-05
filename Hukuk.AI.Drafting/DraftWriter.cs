using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hukuk.AI.Data.Entities;
using Hukuk.AI.Documents;
using Hukuk.AI.Retrieval;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Hukuk.AI.Drafting;

// Denetimde kanuna aykırı bulunup yeniden yazılan madde. Text: yeni metin. Reason: aykırılığın açıklaması;
// içindeki [n] numaraları ve Citations, DraftResult.Citations'a karşılık gelir.
public record DraftRevision(string Clause, string Title, string Text, string Reason, int[] Citations);

// Maddenin kanun dayanağı (iskeletteki sabit liste), örn: "md. 6", "Güvence Bedeli", ["TBK md. 342"].
public record ClauseBasis(string Clause, string Title, string[] Articles);

// Draft: denetimden geçmiş son metin (markdown). Unresolved: denetimin bulduğu ama düzeltilemeyen riskler.
public record DraftResult(string Draft, List<DraftRevision> Revisions, List<ReportRisk> Unresolved,
    List<ClauseBasis> Basis, List<KnowledgeSource> Citations);

// Streaming olayları: ilk taslak parça parça, denetim başlarken haber, düzeltilen her madde için bir olay.
public record DraftEvents(Func<string, Task> OnDelta, Func<Task> OnChecking, Func<DraftRevision, Task> OnRevision);

// Sözleşme taslağı (kararlar 2026-10-05): sabit pipeline, function calling yok.
//   1) İskeletteki bölümlerin kanun maddeleri doğrudan getirilir (arama yok).
//   2) Taslak tek LLM çağrısında yazılır ve yazıldıkça kullanıcıya akar.
//   3) Taslak, yüklenen belgelerle aynı risk raporundan geçer (RiskReportService).
//   4) Kanuna aykırı bulunan maddeler tek LLM çağrısında yeniden yazılır; tek tur, tekrar denetim yok.
public partial class DraftWriter(KnowledgeSearch search, RiskReportService reports, IChatCompletionService chat,
    ILogger<DraftWriter> logger)
{
    // Promptlardan biri değişince artır (eval sonuç dosyaları buna bağlı).
    // v2 (taslak eval'i, 13 aykırı istekten 8'i gideriliyordu): yazım aynı isteği hem kendi maddesine hem Özel
    //     Hükümler'e yazıyordu, denetim birini düzeltince diğeri kalıyordu; düzeltme aykırı hükmü silmek yerine
    //     "bu hüküm geçersizdir" diye maddede bırakıyordu.
    public const int PromptVersion = 2;

    // Ölçülmedi (geçici varsayılanlar): yazımda minimal, ilk parça hızlı gelsin diye; düzeltmede low.
    public const string WriteReasoningEffort = "minimal";
    public const string FixReasoningEffort = "low";

    public record FixedClause(
        [property: Description("Maddenin numarası")] int No,
        [property: Description("Maddenin düzeltilmiş metni; başlık ve numara olmadan")] string Text);

    public record FixedClauses(List<FixedClause> Clauses);

    private const string WritePrompt =
        """
        Sen Türk hukukuna göre sözleşme taslağı hazırlayan bir hukuk asistanısın. Sözleşmenin türü, kullanıcının verdiği
        bilgiler, yazılacak bölümler ve numaralı kanun kaynakları verilecek.
        Biçim:
        - İlk satır sözleşmenin adı, örn: "# KİRA SÖZLEŞMESİ".
        - Her bölüm bir madde: verilen sırayla, verilen başlıkla, örn: "## MADDE 1 – Taraflar". Bölüm ekleme,
          birleştirme; başlığı değiştirme.
        - Son maddeden sonra "## İmzalar" başlığı altında tarafların imza bloğu.
        - Sadece sözleşme metnini yaz: açıklama, not ya da köşeli parantezli kaynak numarası ekleme. Metinde bir kanun
          maddesini anacaksan sadece verilen kaynaklardaki madde numaralarını kullan.
        Kurallar:
        - Kullanıcının açıkça verdiği bilgileri (taraflar, adres, tutar, süre, oran, özel istekler) aynen kullan,
          değiştirme; bunların kanuna uygunluğu ayrıca denetlenecek.
        - Her bilgi ve istek sadece BİR maddede yer alır: konusunu düzenleyen bölümde (izin süresi Yıllık Ücretli İzin
          bölümünde, gecikme yaptırımı Kiracının Temerrüdü bölümünde gibi). "Özel Hükümler" bölümüne yalnızca başka
          hiçbir bölümün konusuna girmeyen istekler yazılır; başka maddede yazılanı orada tekrarlama. Oraya yazılacak
          istek kalmadıysa "Özel Hükümler" bölümünü atla ve numaralandırmayı kaydır.
        - Kullanıcının belirtmediği konularda bölümün kaynaklarındaki hükümlere uygun, iki taraf için dengeli maddeler
          yaz. Bu konularda kaynaklardaki emredici hükümlere aykırı ya da bir tarafın kanundan doğan hakkını kaldıran
          madde yazma.
        - Bilgi uydurma: verilmemiş ama metinde gereken bilgi (kimlik numarası, adres, IBAN gibi) için "[..........]" bırak.
        - Kaynaklarda olmayan hukuki kural ekleme.
        - Resmi ve sade sözleşme dili; maddeler kısa, numaralı fıkralar halinde.
        """;

    private const string FixPrompt =
        """
        Sen sözleşme taslağı düzelten bir hukuk asistanısın. Kanuna aykırı bulunan sözleşme maddeleri, aykırılığın
        açıklaması, düzeltme önerisi ve numaralı kanun kaynakları verilecek. Her maddeyi kaynaklardaki hükme uygun hale
        getirerek yeniden yaz.
        Kurallar:
        - Sadece aykırı kısmı değiştir; maddenin geri kalanını, tarafları, diğer tutar ve süreleri ve üslubu koru.
        - Aykırı hükmün yerine kaynaktaki kuralı yaz (süre ya da tutar sınırı aşıyorsa kanundaki sınıra indir).
          Kaynak o tür bir hükmü tümüyle yasaklıyorsa hükmü maddeden sil; "bu hüküm geçersizdir", "kanuna aykırı
          olmamak kaydıyla" gibi açıklama ya da çekince yazma. Kalan fıkraları yeniden numarala.
        - Text alanına sadece madde metnini yaz; başlık, numara, açıklama ya da kaynak numarası ekleme.
        - Verilen her madde için bir kayıt döndür.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private record Clause(int No, string Title, int BodyStart, int BodyEnd);

    // events verilirse taslak yazıldıkça ve düzeltildikçe bildirilir (SSE).
    public async Task<DraftResult> WriteAsync(DraftTemplate template, IReadOnlyDictionary<string, string> fields,
        DraftEvents? events = null, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var sections = template.Sections.Where(s => s.OnlyIfField is null || fields.ContainsKey(s.OnlyIfField)).ToList();
        var sources = await search.GetArticlesAsync(sections.SelectMany(s => s.Articles).Distinct()
            .Select(no => new QueryRouter.LegalReference(template.Law, ArticleType.Asil, no)));
        var searchMs = sw.ElapsedMilliseconds;

        var history = new ChatHistory(WritePrompt);
        history.AddUserMessage(BuildWriteMessage(template, fields, sections, sources));
        var settings = new OpenAIPromptExecutionSettings { ReasoningEffort = WriteReasoningEffort };
        var text = new StringBuilder();
        await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(history, settings, cancellationToken: cancellationToken))
        {
            if (string.IsNullOrEmpty(chunk.Content))
                continue;
            if (text.Length == 0)
                logger.LogInformation("İlk token {FirstTokenMs} ms", sw.ElapsedMilliseconds);
            text.Append(chunk.Content);
            if (events is not null)
                await events.OnDelta(chunk.Content);
        }
        var draft = text.ToString().Trim() is { Length: > 0 } t ? t : throw new InvalidOperationException("Boş yanıt.");
        var writeMs = sw.ElapsedMilliseconds - searchMs;

        if (events is not null)
            await events.OnChecking();
        var report = await reports.GenerateAsync(draft, cancellationToken: cancellationToken);
        var checkMs = sw.ElapsedMilliseconds - searchMs - writeMs;

        var (final, revisions, unresolved) = await FixAsync(draft, report, events, cancellationToken);
        logger.LogInformation("Taslak ({Type}): {Sources} kanun kaynağı ({SourceTokens} token), {Revisions} madde düzeltildi, {Unresolved} risk kaldı; maddeler {SearchMs} ms, yazım {WriteMs} ms, denetim {CheckMs} ms, düzeltme {FixMs} ms",
            template.Type, sources.Count, sources.Sum(s => s.TokenCount), revisions.Count, unresolved.Count,
            searchMs, writeMs, checkMs, sw.ElapsedMilliseconds - searchMs - writeMs - checkMs);

        return new DraftResult(final, revisions, unresolved, BuildBasis(final, sections, sources), report.Citations);
    }

    // Kanun dayanağı olan riskler düzeltilir; maddesi taslakta bulunamayan ya da LLM'in geri döndürmediği risk kalır.
    private async Task<(string Draft, List<DraftRevision> Revisions, List<ReportRisk> Unresolved)> FixAsync(
        string draft, RiskReport report, DraftEvents? events, CancellationToken cancellationToken)
    {
        var clauses = ParseClauses(draft);
        var flagged = report.Risks.Where(r => r.HasLegalBasis)
            .Select(r => (Risk: r, Clause: clauses.FirstOrDefault(c => c.No == ClauseNo(r.Clause))))
            .Where(x => x.Clause is not null)
            .GroupBy(x => x.Clause!, x => x.Risk)
            .ToList();
        if (flagged.Count == 0)
            return (draft, [], report.Risks);

        var history = new ChatHistory(FixPrompt);
        history.AddUserMessage(BuildFixMessage(draft, flagged, report.Citations));
        var settings = new OpenAIPromptExecutionSettings { ReasoningEffort = FixReasoningEffort, ResponseFormat = typeof(FixedClauses) };
        var reply = await chat.GetChatMessageContentAsync(history, settings, cancellationToken: cancellationToken);
        var rewritten = JsonSerializer.Deserialize<FixedClauses>(reply.Content ?? "", JsonOptions) ?? throw new InvalidOperationException("Boş yanıt.");

        var revisions = new List<DraftRevision>();
        var resolved = new List<ReportRisk>();
        // Sondan başa: öndeki maddelerin konumu kaymasın.
        foreach (var group in flagged.OrderByDescending(g => g.Key.BodyStart))
        {
            var clause = group.Key;
            var fix = rewritten.Clauses.FirstOrDefault(f => f.No == clause.No);
            if (string.IsNullOrWhiteSpace(fix?.Text))
                continue;
            draft = $"{draft[..clause.BodyStart]}\n{fix.Text.Trim()}\n\n{draft[clause.BodyEnd..]}".TrimEnd();
            resolved.AddRange(group);
            revisions.Insert(0, new DraftRevision($"md. {clause.No}", clause.Title, fix.Text.Trim(),
                string.Join(" ", group.Select(r => r.Explanation)), [.. group.SelectMany(r => r.Citations).Distinct().Order()]));
        }

        if (events is not null)
            foreach (var revision in revisions)
                await events.OnRevision(revision);
        return (draft, revisions, [.. report.Risks.Except(resolved)]);
    }

    private static string BuildWriteMessage(DraftTemplate template, IReadOnlyDictionary<string, string> fields,
        List<DraftSection> sections, List<KnowledgeSource> sources)
    {
        var sb = new StringBuilder($"Sözleşme türü: {template.Name}\n\nKullanıcının verdiği bilgiler:\n");
        foreach (var field in template.Fields.Where(f => fields.ContainsKey(f.Key)))
            sb.Append($"- {field.Label}: {fields[field.Key]}\n");

        sb.Append("\nBölümler:\n");
        for (var i = 0; i < sections.Count; i++)
        {
            var numbers = SourcesOf(sections[i], sources).Select(s => $"[{s.Number}]").ToList();
            sb.Append($"{i + 1}. {sections[i].Title}: {sections[i].Guidance}");
            sb.Append(numbers.Count > 0 ? $" Kaynaklar: {string.Join(" ", numbers)}\n" : "\n");
        }

        sb.Append("\nKanun kaynakları:\n\n");
        foreach (var s in sources)
            sb.Append($"[{s.Number}] {s.Label}\n{s.Content}\n\n");
        return sb.ToString();
    }

    private static string BuildFixMessage(string draft, List<IGrouping<Clause, ReportRisk>> flagged, List<KnowledgeSource> citations)
    {
        var cited = flagged.SelectMany(g => g).SelectMany(r => r.Citations).ToHashSet();
        var sb = new StringBuilder("Kanun kaynakları:\n\n");
        foreach (var s in citations.Where(c => cited.Contains(c.Number)))
            sb.Append($"[{s.Number}] {s.Label}\n{s.Content}\n\n");

        sb.Append("Düzeltilecek maddeler:\n\n");
        foreach (var group in flagged)
        {
            var clause = group.Key;
            sb.Append($"MADDE {clause.No} – {clause.Title}\n{draft[clause.BodyStart..clause.BodyEnd].Trim()}\n");
            foreach (var risk in group)
                sb.Append($"Aykırılık: {risk.Explanation}\nÖneri: {risk.Recommendation}\n");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // Madde, başlığı iskeletteki bölüm başlığıyla eşleşiyorsa o bölümün maddelerine dayanır.
    private static List<ClauseBasis> BuildBasis(string draft, List<DraftSection> sections, List<KnowledgeSource> sources)
    {
        var basis = new List<ClauseBasis>();
        foreach (var clause in ParseClauses(draft))
        {
            var section = sections.FirstOrDefault(s => string.Equals(s.Title, clause.Title, StringComparison.CurrentCultureIgnoreCase));
            var labels = section is null ? [] : SourcesOf(section, sources).Select(s => s.Label).ToArray();
            if (labels.Length > 0)
                basis.Add(new ClauseBasis($"md. {clause.No}", clause.Title, labels));
        }
        return basis;
    }

    private static IEnumerable<KnowledgeSource> SourcesOf(DraftSection section, List<KnowledgeSource> sources) =>
        sources.Where(s => s.Articles.Any(a => a.Type == ArticleType.Asil && section.Articles.Contains(a.No)));

    // Madde gövdesi: başlık satırından sonraki "## " başlığına (sonraki madde ya da İmzalar) kadar.
    private static List<Clause> ParseClauses(string draft)
    {
        var headings = AnyHeading().Matches(draft).Select(m => m.Index).ToList();
        return ClauseHeading().Matches(draft)
            .Select(m => new Clause(int.Parse(m.Groups[1].Value), m.Groups[2].Value, m.Index + m.Length,
                headings.Where(h => h > m.Index).DefaultIfEmpty(draft.Length).First()))
            .ToList();
    }

    // Rapordaki madde adı ("md. 7", "MADDE 7") → numara; numara yoksa -1.
    private static int ClauseNo(string clause) =>
        Digits().Match(clause) is { Success: true } m ? int.Parse(m.Value) : -1;

    [GeneratedRegex(@"^##\s*MADDE\s+(\d+)\s*[–—:-]\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClauseHeading();

    [GeneratedRegex(@"^##\s", RegexOptions.Multiline)]
    private static partial Regex AnyHeading();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
