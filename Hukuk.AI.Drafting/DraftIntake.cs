using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Hukuk.AI.Drafting;

// Kullanıcının serbest metninden sözleşme türünü ve verdiği bilgileri çıkarır (karar 2026-10-05). Eksik zorunlu
// bilgiler kullanıcıya soru olarak döner (bkz. DraftTemplate.Missing); taslak eksik bilgiyle yazılmaz.
public class DraftIntake(IChatCompletionService chat)
{
    // Prompt değişince artır (eval sonuç dosyaları buna bağlı).
    // v2 (çıkarım eval'i, 24 koşu × 2): low'da 4-5 uydurma (adres yerine "Mağaza", tutar yerine ödeme dönemi alana
    //     yazılıyor, eksik bilgi sorulmuyordu); minimal'de 10-12 kaçak (cümleden anlaşılan görev çıkarılmıyordu).
    //     Alanların yanına soruları eklendi, "metin soruyu cevaplıyor mu" ölçütü getirildi.
    public const int PromptVersion = 2;

    // Çıkarım eval'i (2026-10-05, prompt v2, 8 istek × 3 tekrar, iki koşu, "-- taslak cikarim"): minimal 19/24 ve
    // 17/24 tam doğru (5-7 kaçan alan), 2.5 sn; low 24/24 ve 23/24, 5 sn; medium 24/24 ve 24/24, 11 sn. Uydurma yok.
    public const string ReasoningEffort = "low";

    public const string OutOfScope = "kapsam_disi";

    public record ExtractedField(
        [property: Description("Alanın anahtarı, listedeki haliyle")] string Key,
        [property: Description("Kullanıcının verdiği değer, kendi ifadesiyle")] string Value);

    public record Extraction(
        [property: Description("kira | is | kapsam_disi")] string Type,
        List<ExtractedField> Fields);

    private static readonly string SystemPrompt = BuildPrompt();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Tür kapsam dışıysa Template null. effort: canlıda varsayılan; eval alternatifleri karşılaştırır ("-- taslak cikarim").
    public async Task<(DraftTemplate? Template, Dictionary<string, string> Fields)> ExtractAsync(string request,
        CancellationToken cancellationToken = default, string effort = ReasoningEffort)
    {
        var history = new ChatHistory(SystemPrompt);
        history.AddUserMessage(request);
        var settings = new OpenAIPromptExecutionSettings { ReasoningEffort = effort, ResponseFormat = typeof(Extraction) };
        var reply = await chat.GetChatMessageContentAsync(history, settings, cancellationToken: cancellationToken);
        var extraction = JsonSerializer.Deserialize<Extraction>(reply.Content ?? "", JsonOptions) ?? throw new InvalidOperationException("Boş yanıt.");

        var template = DraftTemplates.Find(extraction.Type);
        return (template, template is null ? [] : Clean(template, extraction.Fields.Select(f => KeyValuePair.Create(f.Key, f.Value))));
    }

    // Şablonda olmayan anahtarlar ve boş değerler atılır (LLM çıktısı ve kullanıcı cevapları için ortak).
    public static Dictionary<string, string> Clean(DraftTemplate template, IEnumerable<KeyValuePair<string, string>> values)
    {
        var fields = new Dictionary<string, string>();
        foreach (var (key, value) in values)
            if (template.Fields.Any(f => f.Key == key) && !string.IsNullOrWhiteSpace(value))
                fields[key] = value.Trim();
        return fields;
    }

    private static string BuildPrompt()
    {
        var sb = new StringBuilder(
            """
            Sen sözleşme taslağı hazırlayan bir hukuk asistanının ön büro görevlisisin. Kullanıcının isteğinden hangi
            sözleşmenin istendiğini ve kullanıcının verdiği bilgileri çıkar.
            Kurallar:
            - Type: aşağıdaki türlerden biri. İstek bunlardan biri değilse (satış sözleşmesi, dilekçe, ihtarname gibi)
              ya da sözleşme taslağı istemiyorsa kapsam_disi.
            - Fields: sadece kullanıcının verdiği bilgiler. Metinde olmayan bilgiyi tahmin etme, varsayılan değer
              yazma; verilmeyen alanı listeye hiç ekleme.
            - Her alanın yanında cevaplaması gereken soru var. Alanı ancak metin o soruyu cevaplıyorsa ekle: adres
              sorusuna "dükkanım", tutar sorusuna "maaşı elden vereceğim" cevap değildir, alan eklenmez (kullanıcıya
              sorulacak). Bilgi ayrı bir cümleyle söylenmemiş ama metinden kesin anlaşılıyorsa ekle ("aşçı arıyorum"
              görev sorusunun cevabıdır; "süresiz çalışacak" sözleşme süresi sorusunun cevabıdır).
            - Bir alanın sorusunu cevaplamayan ama sözleşmeye girmesi istenen istekleri (ödeme dönemi, yaptırım,
              yasak gibi) ozel_istekler alanında topla.
            Türler ve alanları:

            """);
        foreach (var template in DraftTemplates.All)
        {
            sb.Append($"{template.Type} ({template.Name}):\n");
            foreach (var field in template.Fields)
                sb.Append($"- {field.Key}: {field.Label}. Soru: {field.Question}\n");
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
