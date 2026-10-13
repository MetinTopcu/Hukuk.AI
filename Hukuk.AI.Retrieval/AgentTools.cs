using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Hukuk.AI.Retrieval;

// Number cevaptaki atıf numarası: [W1], [W2] ...
public record WebSource(int Number, string Title, string Url, string Content);

public record ToolCall(string Tool, string Query);

// Agent modunda LLM'in çağırabildiği araçlar. Bir soru için bir örnek: model aynı aracı birden çok kez çağırabildiği
// için kaynak numaraları burada tutulur (bilgi tabanı [1], [2] ...; web [W1], [W2] ...) ve çağrılar arasında çakışmaz.
public class AgentTools(KnowledgeSearch search, WebSearchClient? web, ILogger logger, Func<ToolCall, Task>? onCall = null)
{
    public const string PluginName = "Tools";
    public const string KnowledgeFunction = KnowledgeBasePlugin.SearchFunction;
    public const string WebFunction = "search_web";
    public const int MaxCalls = 4; // model döngüye girerse dursun

    public List<KnowledgeSource> KnowledgeSources { get; } = [];
    public List<WebSource> WebSources { get; } = [];
    public List<ToolCall> Calls { get; } = [];

    [KernelFunction(KnowledgeFunction)]
    [Description("Kanun metni bilgi tabanında arama yapar. İçinde SADECE Türk Borçlar Kanunu'nun kira hükümleri (md. 299-378) ve İş Kanunu (4857) vardır. Kira ve iş hukuku sorularında önce bunu kullan.")]
    public async Task<string> SearchKnowledgeBaseAsync(
        [Description("Aranacak soru, kullanıcının ifadesiyle")] string question,
        CancellationToken cancellationToken)
    {
        if (await RegisterAsync(KnowledgeFunction, question) is { } limit)
            return limit;

        var sb = new StringBuilder();
        foreach (var found in await search.SearchAsync(question, cancellationToken))
        {
            var source = KnowledgeSources.FirstOrDefault(s => s.Content == found.Content);
            if (source is null)
                KnowledgeSources.Add(source = found with { Number = KnowledgeSources.Count + 1 });
            sb.Append($"[{source.Number}] {source.Label}\n{source.Content}\n\n");
        }
        return sb.Length > 0 ? sb.ToString() : "Sonuç yok.";
    }

    [KernelFunction(WebFunction)]
    [Description("İnternette arama yapar. Bilgi tabanında olmayan hukuk konuları (başka kanunlar) ve güncel bilgiler (bu yılın asgari ücreti, kıdem tazminatı tavanı, kira artış oranı, yeni yasa değişiklikleri) için kullan.")]
    public async Task<string> SearchWebAsync(
        [Description("Arama motoruna yazılacak kısa Türkçe sorgu; güncel bilgi aranıyorsa yılı ekle")] string query,
        CancellationToken cancellationToken)
    {
        if (web is null)
            return "Web araması yapılandırılmamış.";
        if (await RegisterAsync(WebFunction, query) is { } limit)
            return limit;

        List<WebResult> results;
        try
        {
            results = await web.SearchAsync(query, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(e, "Web araması başarısız: {Query}", query);
            return "Web araması şu an kullanılamıyor.";
        }

        var sb = new StringBuilder();
        foreach (var found in results)
        {
            var source = WebSources.FirstOrDefault(s => s.Url == found.Url);
            if (source is null)
                WebSources.Add(source = new WebSource(WebSources.Count + 1, found.Title, found.Url, found.Content));
            sb.Append($"[W{source.Number}] {source.Title} ({source.Url})\n{source.Content}\n\n");
        }
        return sb.Length > 0 ? sb.ToString() : "Sonuç yok.";
    }

    // Çağrıyı kaydeder; sınır dolduysa modele dönecek mesajı verir.
    private async Task<string?> RegisterAsync(string tool, string query)
    {
        if (Calls.Count >= MaxCalls)
            return "Arama hakkı doldu; eldeki kaynaklarla cevap ver.";
        var call = new ToolCall(tool, query);
        Calls.Add(call);
        logger.LogInformation("Araç çağrısı: {Tool} \"{Query}\"", tool, query);
        if (onCall is not null)
            await onCall(call);
        return null;
    }
}
