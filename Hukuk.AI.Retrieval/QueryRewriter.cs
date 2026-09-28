using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Hukuk.AI.Retrieval;

// Kullanıcı sorusunu kanun diline çevirir (query rewriting). Prompt değişirse PromptVersion artırılmalı;
// eval'deki rewrite cache'i sürüme bağlı olduğu için eski sonuçlarla karışmaz.
public class QueryRewriter(IChatCompletionService chat)
{
    public const int PromptVersion = 1;

    // Eval setinden türetilmiş örnek eşleştirme İÇERMEZ (ör. "ihbar -> bildirim"); aksi halde ölçüme kopya verilmiş olur.
    public const string SystemPrompt =
        """
        Sen Türk mevzuatında arama yapan bir sistemin sorgu yazıcısısın.
        Kullanıcının günlük dille yazdığı soruyu, ilgili kanun maddesinde geçmesi muhtemel resmi hukuki terim ve kavramlarla yeniden yaz.
        Kurallar:
        - Soruyu cevaplama, yorum ve tavsiye ekleme.
        - Kanun adı, kanun numarası veya madde numarası yazma.
        - Kişileri hukuki sıfatlarıyla an (ör. taraflar, sözleşmedeki rolleri).
        - Sorudaki olayın hukuki niteliğini ve sorulan hakkı/yükümlülüğü belirt.
        - Tek paragraf, en fazla 60 kelime, Türkçe.
        """;

    public async Task<string> RewriteAsync(string question, CancellationToken cancellationToken = default) =>
        await RewriteAsync(question, "low", cancellationToken);

    public async Task<string> RewriteAsync(string question, string reasoningEffort, CancellationToken cancellationToken = default)
    {
        var history = new ChatHistory(SystemPrompt);
        history.AddUserMessage(question);

        // gpt-5-mini reasoning modeli; basit bir dönüşüm için düşük efor yeterli (ölçülen rewrites-v1 "low" ile üretildi).
        var settings = new OpenAIPromptExecutionSettings { ReasoningEffort = reasoningEffort };
        var reply = await chat.GetChatMessageContentAsync(history, settings, cancellationToken: cancellationToken);
        return reply.Content?.Trim() ?? throw new InvalidOperationException("Boş yanıt.");
    }

    // Aramada kullanılan sorgu metni ("Birlesik"): soru + kanun diline yeniden yazılmış hali.
    // Ölçümde sadece soru ve sadece yeniden yazımdan daha iyi çıktı.
    public static string Combine(string question, string rewrite) => question + "\n" + rewrite;
}
