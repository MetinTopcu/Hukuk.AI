using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Hukuk.AI.Evaluation;

// Kullanıcı sorusunu kanun diline çevirir (query rewriting). LLM çıktısı her çağrıda değişebildiği için
// sonuçlar dosyada saklanır; ölçümler bu dosyayla tekrarlanabilir olur. Prompt değişirse PromptVersion artırılmalı.
public class QueryRewriter(IChatCompletionService chat, string cachePath)
{
    public const int PromptVersion = 1;

    // Eval setinden türetilmiş örnek eşleştirme İÇERMEZ (ör. "ihbar -> bildirim"); aksi halde ölçüme kopya verilmiş olur.
    private const string SystemPrompt =
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<Dictionary<string, string>> RewriteAllAsync(IEnumerable<EvalQuestion> questions)
    {
        var cache = File.Exists(cachePath)
            ? JsonSerializer.Deserialize<RewriteCache>(File.ReadAllText(cachePath))!
            : new RewriteCache(PromptVersion, SystemPrompt, []);

        if (cache.PromptVersion != PromptVersion)
            throw new InvalidOperationException($"{cachePath} prompt v{cache.PromptVersion} ile üretilmiş; güncel v{PromptVersion}. Dosyayı silip yeniden üretin.");

        foreach (var q in questions.Where(q => !cache.Rewrites.ContainsKey(q.Id)))
        {
            cache.Rewrites[q.Id] = await RewriteAsync(q.Question);
            Console.WriteLine($"  {q.Id}: {cache.Rewrites[q.Id]}");
            File.WriteAllText(cachePath, JsonSerializer.Serialize(cache, JsonOptions)); // yarıda kesilirse kaybolmasın
        }

        return cache.Rewrites;
    }

    private async Task<string> RewriteAsync(string question)
    {
        var history = new ChatHistory(SystemPrompt);
        history.AddUserMessage(question);

        // gpt-5-mini reasoning modeli; basit bir dönüşüm için düşük efor yeterli.
        var settings = new OpenAIPromptExecutionSettings { ReasoningEffort = "low" };
        var reply = await chat.GetChatMessageContentAsync(history, settings);
        return reply.Content?.Trim() ?? throw new InvalidOperationException("Boş yanıt.");
    }

    private record RewriteCache(int PromptVersion, string SystemPrompt, Dictionary<string, string> Rewrites);
}
