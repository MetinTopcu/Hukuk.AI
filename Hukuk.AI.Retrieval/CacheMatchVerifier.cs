using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Hukuk.AI.Retrieval;

// Semantic cache doğrulayıcısı: embedding benzerliği tek başına yetmiyor (2026-09-29 ölçümü: "TBK 348" sorusu 347'ye
// .946, "5 yıl" ihbar sorusu "2 yıl"a .943 benzer; aynı anlamlı soruların çoğu ise .80-.90). Aday bulununca LLM'e
// "eski cevap yeni soruya da aynen doğru mu?" diye sorulur; emin değilse HAYIR (yanlış cevap, cache kaçırmaktan kötü).
// Önce deterministik sayı kontrolü: LLM (minimal) "6 ay" / "2 ay" deneme süresi farkını kaçırdı; sayılar farklıysa
// LLM'e hiç sorulmaz (hem güvenli hem bedava).
public partial class CacheMatchVerifier(IChatCompletionService chat)
{
    public const int PromptVersion = 1;

    // Eval'deki tuzak sorulardan türetilmiş örnek İÇERMEZ; sadece genel ayrım ölçütleri (ölçüme kopya verilmesin).
    private const string SystemPrompt =
        """
        Sen bir hukuk soru-cevap sisteminin önbellek denetçisisin. İki kullanıcı sorusu verilecek.
        Karar ver: ÖNCEKİ soru için yazılmış cevap, YENİ soruya hiçbir değişiklik yapılmadan doğru ve eksiksiz bir cevap olur mu?
        Şunlardan biri farklıysa cevap HAYIR:
        - Sayılar, süreler, tutarlar, oranlar, yaş, kıdem (birimi dahil)
        - Kanun veya madde numarası
        - Taraflar ve rolleri (kim kime karşı, kimin hakkı soruluyor)
        - Olay, sebep veya koşul
        - Sorulan hak, yükümlülük veya sonuç
        Sadece ifade, yazım, kelime sırası veya üslup farklıysa cevap EVET.
        Emin değilsen HAYIR.
        Sadece tek kelime yaz: EVET veya HAYIR.
        """;

    public async Task<bool> IsSameAnswerAsync(string cachedQuestion, string newQuestion, CancellationToken cancellationToken = default)
    {
        if (!Numbers(cachedQuestion).SetEquals(Numbers(newQuestion)))
            return false;

        var history = new ChatHistory(SystemPrompt);
        history.AddUserMessage($"ÖNCEKİ soru: {cachedQuestion}\nYENİ soru: {newQuestion}");
        var settings = new OpenAIPromptExecutionSettings { ReasoningEffort = "minimal" };
        var reply = await chat.GetChatMessageContentAsync(history, settings, cancellationToken: cancellationToken);
        return reply.Content?.Trim().StartsWith("EVET", StringComparison.OrdinalIgnoreCase) == true;
    }

    // Sorudaki sayılar: rakamlar + yazıyla sayılar ("altı ay", "iki yıllık" -> 6, 2). "bir" belirsizlik için alınmaz
    // ("bir işçi"); yazıyla sayı ancak ardından boşluk veya -lık/-lik eki gelirse sayılır ("altında", "onay" sayılmaz).
    // Kanun numaraları (6098, 4857) sayılmaz: "TBK 344" ile "6098 sayılı Kanun 344" aynı soru.
    public static HashSet<int> Numbers(string text)
    {
        var numbers = Digits().Matches(text).Select(m => int.Parse(m.Value)).Where(n => n is not (6098 or 4857)).ToHashSet();
        foreach (Match m in NumberWords().Matches(text.ToLower(new System.Globalization.CultureInfo("tr-TR"))))
            numbers.Add(WordValues[m.Groups[1].Value]);
        return numbers;
    }

    private static readonly Dictionary<string, int> WordValues = new()
    {
        ["iki"] = 2, ["üç"] = 3, ["dört"] = 4, ["beş"] = 5, ["altı"] = 6, ["yedi"] = 7, ["sekiz"] = 8, ["dokuz"] = 9,
        ["on"] = 10, ["yirmi"] = 20, ["otuz"] = 30, ["kırk"] = 40, ["elli"] = 50, ["altmış"] = 60,
    };

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\b(iki|üç|dört|beş|altı|yedi|sekiz|dokuz|on|yirmi|otuz|kırk|elli|altmış)(?=\s|lık|lik|luk|lük)")]
    private static partial Regex NumberWords();
}
