using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Hukuk.AI.Retrieval;

public record LegalAnswer(string Answer, List<KnowledgeSource> Citations);

// Sabit RAG pipeline'ı: bilgi tabanı araması -> kaynaklarla cevap -> cevapta atıf yapılan kaynaklar.
public partial class LegalAnswerService
{
    private const string SystemPrompt =
        """
        Sen Türk mevzuatı hakkında bilgi veren bir hukuk asistanısın. Soruyu SADECE verilen kaynaklara dayanarak cevapla.
        Kurallar:
        - Her hukuki bilginin sonuna dayandığı kaynağın numarasını köşeli parantezle yaz, örn: [1] veya [1][3].
        - Kaynaklarda olmayan bilgi ekleme. Kaynaklar soruyu cevaplamaya yetmiyorsa bunu açıkça söyle.
        - Madde metnini aynen kopyalama; sade Türkçeyle açıkla, gerekirse kısa alıntı yap.
        - Cevap somut olayın ayrıntılarına göre değişebiliyorsa hangi koşula bağlı olduğunu belirt.
        - Kısa ve net ol.
        """;

    private readonly Kernel _kernel;
    private readonly IChatCompletionService _chat;

    public LegalAnswerService(Kernel kernel, KnowledgeBasePlugin knowledgeBase)
    {
        // Kernel transient (her istekte yeni örnek); scoped plugin'i (DbContext kullanıyor) sadece bu örneğe ekliyoruz.
        _kernel = kernel;
        _kernel.Plugins.AddFromObject(knowledgeBase, KnowledgeBasePlugin.PluginName);
        _chat = kernel.GetRequiredService<IChatCompletionService>();
    }

    public async Task<LegalAnswer> AnswerAsync(string question, CancellationToken cancellationToken = default)
    {
        // Kernel üzerinden çağrı: SK filtreleri ve telemetri (ileride loglama, önbellek) bu çağrıyı da görür.
        var sources = await _kernel.InvokeAsync<List<KnowledgeSource>>(
            KnowledgeBasePlugin.PluginName, KnowledgeBasePlugin.SearchFunction,
            new KernelArguments { ["question"] = question }, cancellationToken) ?? [];

        var history = new ChatHistory(SystemPrompt);
        history.AddUserMessage(BuildUserMessage(question, sources));
        var reply = await _chat.GetChatMessageContentAsync(history, kernel: _kernel, cancellationToken: cancellationToken);
        var answer = reply.Content?.Trim() ?? throw new InvalidOperationException("Boş yanıt.");

        // Sadece cevapta gerçekten atıf yapılan kaynaklar döner; LLM'in uydurduğu numaralar ([9] gibi) elenir.
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
