using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Hukuk.AI.Retrieval;

public record LegalAnswer(string Answer, List<KnowledgeSource> Citations);

// Sabit RAG pipeline'ı: bilgi tabanı araması -> kaynaklarla cevap -> cevapta atıf yapılan kaynaklar.
public partial class LegalAnswerService
{
    // v2: kapsam dışı sorularda ilgisiz kaynakları aktarıp hepsine atıf yapıyordu ("trafik cezası" -> İş K. idari para cezaları).
    public const int PromptVersion = 2;

    // Cevap eval'i (2026-09-27, bütçe 4000): minimal puan .933 / 3.3 sn, low .933 / 5.3 sn, medium .963 / 11.9 sn.
    // Fark gürültü sınırında (67 soruda ~4 puan, hakem de aynı model); hız için minimal seçildi.
    public const string AnswerReasoningEffort = "minimal";

    private const string SystemPrompt =
        """
        Sen Türk mevzuatı hakkında bilgi veren bir hukuk asistanısın. Soruyu SADECE verilen kaynaklara dayanarak cevapla.
        Kurallar:
        - Her hukuki bilginin sonuna dayandığı kaynağın numarasını köşeli parantezle yaz, örn: [1] veya [1][3].
        - Kaynaklarda olmayan bilgi ekleme. Kaynaklar soruyu cevaplamaya yetmiyorsa bunu açıkça söyle.
        - Kaynaklar sorunun konusuyla ilgili değilse sadece bu konuda bilgi veremediğini söyle; kaynaklardaki ilgisiz
          hükümleri aktarma, atıf yapma, konuyu değiştirme.
        - Madde metnini aynen kopyalama; sade Türkçeyle açıkla, gerekirse kısa alıntı yap.
        - Cevap somut olayın ayrıntılarına göre değişebiliyorsa hangi koşula bağlı olduğunu belirt.
        - Kısa ve net ol.
        """;

    private readonly Kernel _kernel;
    private readonly IChatCompletionService _chat;
    private readonly ILogger<LegalAnswerService> _logger;

    public LegalAnswerService(Kernel kernel, KnowledgeBasePlugin knowledgeBase, ILogger<LegalAnswerService> logger)
    {
        // Kernel transient (her istekte yeni örnek); scoped plugin'i (DbContext kullanıyor) sadece bu örneğe ekliyoruz.
        _kernel = kernel;
        _kernel.Plugins.AddFromObject(knowledgeBase, KnowledgeBasePlugin.PluginName);
        _chat = kernel.GetRequiredService<IChatCompletionService>();
        _logger = logger;
    }

    // onDelta verilirse cevap parça parça üretilir (streaming): ilk kelime ~1 sn'de kullanıcıya gider.
    public async Task<LegalAnswer> AnswerAsync(string question, Func<string, Task>? onDelta = null, CancellationToken cancellationToken = default)
    {
        // Kernel üzerinden çağrı: SK filtreleri ve telemetri (ileride loglama, önbellek) bu çağrıyı da görür.
        var sources = await _kernel.InvokeAsync<List<KnowledgeSource>>(
            KnowledgeBasePlugin.PluginName, KnowledgeBasePlugin.SearchFunction,
            new KernelArguments { ["question"] = question }, cancellationToken) ?? [];

        var sw = Stopwatch.StartNew();
        var result = onDelta is null
            ? await GenerateAsync(question, sources, AnswerReasoningEffort, cancellationToken)
            : await GenerateStreamingAsync(question, sources, onDelta, cancellationToken);
        _logger.LogInformation("Cevap üretimi {AnswerMs} ms", sw.ElapsedMilliseconds);
        return result;
    }

    // Kaynaklar hazırken sadece cevap üretimi (eval farklı bütçe/reasoning ayarlarını bununla karşılaştırır).
    // reasoningEffort null: modelin varsayılanı (gpt-5-mini'de medium).
    public async Task<LegalAnswer> GenerateAsync(string question, List<KnowledgeSource> sources, string? reasoningEffort = null, CancellationToken cancellationToken = default)
    {
        var settings = new OpenAIPromptExecutionSettings { ReasoningEffort = reasoningEffort };
        var reply = await _chat.GetChatMessageContentAsync(BuildHistory(question, sources), settings, _kernel, cancellationToken);
        return WithCitations(reply.Content, sources);
    }

    private async Task<LegalAnswer> GenerateStreamingAsync(string question, List<KnowledgeSource> sources, Func<string, Task> onDelta, CancellationToken cancellationToken)
    {
        var settings = new OpenAIPromptExecutionSettings { ReasoningEffort = AnswerReasoningEffort };
        var sw = Stopwatch.StartNew();
        var answer = new StringBuilder();
        await foreach (var chunk in _chat.GetStreamingChatMessageContentsAsync(BuildHistory(question, sources), settings, _kernel, cancellationToken))
        {
            if (string.IsNullOrEmpty(chunk.Content))
                continue;
            if (answer.Length == 0)
                _logger.LogInformation("İlk token {FirstTokenMs} ms", sw.ElapsedMilliseconds);
            answer.Append(chunk.Content);
            await onDelta(chunk.Content);
        }
        return WithCitations(answer.ToString(), sources);
    }

    private static ChatHistory BuildHistory(string question, List<KnowledgeSource> sources)
    {
        var history = new ChatHistory(SystemPrompt);
        history.AddUserMessage(BuildUserMessage(question, sources));
        return history;
    }

    // Sadece cevapta gerçekten atıf yapılan kaynaklar döner; LLM'in uydurduğu numaralar ([9] gibi) elenir.
    private static LegalAnswer WithCitations(string? content, List<KnowledgeSource> sources)
    {
        var answer = content?.Trim() is { Length: > 0 } text ? text : throw new InvalidOperationException("Boş yanıt.");
        var cited = CitationMarker().Matches(answer).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
        return new LegalAnswer(answer, sources.Where(s => cited.Contains(s.Number)).ToList());
    }

    private static string BuildUserMessage(string question, List<KnowledgeSource> sources)
    {
        var sb = new StringBuilder("Kaynaklar:\n\n");
        foreach (var s in sources)
            sb.Append($"[{s.Number}] {s.Label}\n{s.Content}\n\n");
        sb.Append($"Soru: {question}");
        return sb.ToString();
    }

    [GeneratedRegex(@"\[(\d+)\]")]
    private static partial Regex CitationMarker();
}
