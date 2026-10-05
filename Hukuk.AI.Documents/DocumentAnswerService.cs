using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Hukuk.AI.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Pgvector;

namespace Hukuk.AI.Documents;

// Cevapta atıf yapılan belge parçası. Number cevaptaki atıf numarası; Label "md. 5" ya da bölüm başlığı.
public record DocumentSource(int Number, string Label, string? Heading, int Page, string Content, int TokenCount);

// DocumentCitations: belgeden, Citations: bilgi tabanından (kanun). Numaralar ortak: önce belge parçaları, sonra kanun.
// KnowledgeContext: LLM'e verilen bütün kanun kaynakları (eval: madde aramada mı kaçtı, cevapta mı kullanılmadı).
public record DocumentAnswer(string Answer, List<DocumentSource> DocumentCitations, List<KnowledgeSource> Citations,
    List<KnowledgeSource> KnowledgeContext);

// Yüklenen belge üzerinde soru-cevap (kararlar 2026-10-01): sabit pipeline, function calling yok, cache yok
// (belge oturumluk; aynı soru farklı belgede farklı cevap).
//   1) Belge bağlamı: belge küçükse tamamı, büyükse soruya en yakın parçalar (Redis vektör araması, sadece bu belge).
//   2) Kanun bağlamı: bilgi tabanı "soru + soruya en yakın belge parçası" ile aranır ("bu madde geçerli mi" sorusu
//      tek başına hangi konunun arandığını söylemez; konu maddenin metninde).
//   3) Cevap iki bağlamla üretilir, belgeye ve kanuna ayrı ayrı atıf yapar.
public partial class DocumentAnswerService(DocumentStore store, QueryEmbedder embedder,
    IEmbeddingGenerator<string, Embedding<float>> embeddings, KnowledgeSearch search, IChatCompletionService chat,
    ILogger<DocumentAnswerService> logger)
{
    // Prompt değişince artır (eval sonuç dosyaları buna bağlı).
    // v2: kanun atfı gerekmeyen 11 sorunun 8'inde ilgisiz kanun maddelerine atıf yapıyordu ("kaynaklarda yok" derken
    //     kaynak numaralarını sayıyor, sadece belgeyi soran soruya kanun hükmü ekliyordu).
    public const int PromptVersion = 2;

    // Belge bu kadar token'a sığıyorsa tamamı verilir (2 sayfalık sözleşme ~1500 token); sığmıyorsa en yakın
    // parçalar bu bütçeye kadar. Kanun bağlamı ayrıca KnowledgeSearch.TokenBudget kadar.
    public const int DocumentTokenBudget = 6000;

    // Belge soru eval'i (2026-10-01, 30 soru, prompt v1): minimal puan .933 / 4.3 sn, low .933 / 6.4 sn,
    // medium .917 / 13.4 sn. Fark yok (1 soru = .033); hız için minimal.
    // Kanun araması sadece soruyla yapılınca: puan .917, kanun atfı .842 (soru + parça ile .895).
    public const string AnswerReasoningEffort = "minimal";

    private const string SystemPrompt =
        """
        Sen kullanıcının yüklediği belge (sözleşme, dilekçe gibi) hakkındaki soruları cevaplayan bir hukuk asistanısın.
        Numaralı belge parçaları ve numaralı kanun kaynakları verilecek. Soruyu SADECE bunlara dayanarak cevapla.
        Kurallar:
        - Belgede ne yazdığını belge parçalarından, kanunun ne dediğini kanun kaynaklarından aktar. Her bilginin sonuna
          dayandığı parçanın ya da kaynağın numarasını köşeli parantezle yaz, örn: [2] veya [2][7].
        - Soru bir maddenin geçerliliğini ya da kanuna uygunluğunu soruyorsa maddeyi kanun kaynaklarıyla karşılaştır.
        - Cevapladığın belge maddesi kanun kaynaklarındaki bir hükme aykırıysa, soru bunu sormasa bile belirt.
          Aykırılık yoksa ve soru kanunu sormuyorsa kanun kaynaklarından bilgi ekleme, onlara atıf yapma.
        - Kanun kaynakları sorunun konusunu düzenlemiyorsa kanun açısından bilgi veremediğini tek cümleyle söyle;
          ilgisiz hükümleri aktarma, ne içerdiklerini sayma. Bir şeyin kaynaklarda OLMADIĞINI söylerken kaynak numarası yazma.
        - Atıf sadece o cümledeki bilginin gerçekten alındığı parça ya da kaynak içindir; aralık yazma ([3-8] gibi).
        - Belgede yazmayan bilgi ekleme. Sorunun cevabı belgede yoksa bunu açıkça söyle.
        - Kaynaklarda olmayan hukuki bilgi ekleme.
        - Sade Türkçe, kısa ve net.
        """;

    // Canlıda varsayılan kullanılır; eval alternatifleri bununla karşılaştırır ("dotnet run -- belgesoru matris").
    // QueryWithChunk false: bilgi tabanı sadece soruyla aranır.
    public record Settings(string AnswerEffort = AnswerReasoningEffort, bool QueryWithChunk = true);

    // onDelta verilirse cevap parça parça üretilir (streaming). chunkCount: DocumentInfo.Chunks.
    public async Task<DocumentAnswer> AnswerAsync(string id, int chunkCount, string question, Func<string, Task>? onDelta = null,
        CancellationToken cancellationToken = default, Settings? answerSettings = null)
    {
        answerSettings ??= new Settings();
        var sw = Stopwatch.StartNew();
        var chunks = await store.GetChunksAsync(id, chunkCount);
        if (chunks.Count == 0)
            throw new InvalidOperationException($"Belge {id} parçaları bulunamadı.");

        var whole = chunks.Sum(c => c.TokenCount) <= DocumentTokenBudget;
        var questionVector = await embedder.EmbedAsync(question, cancellationToken);
        // Belgenin tamamı veriliyorsa sadece en yakın parça gerekir (kanun araması için).
        var ranked = (await store.SearchChunksAsync(id, questionVector, whole ? 1 : chunks.Count))
            .Select(r => chunks.FirstOrDefault(c => c.Index == r.Index)).OfType<DocumentChunk>().ToList();
        var context = whole ? chunks : [.. WithinBudget(ranked).OrderBy(c => c.Index)]; // LLM belge sırasıyla okusun
        var documentMs = sw.ElapsedMilliseconds;

        var knowledge = await SearchKnowledgeAsync(question, answerSettings.QueryWithChunk ? ranked.FirstOrDefault() : null, cancellationToken);
        var searchMs = sw.ElapsedMilliseconds - documentMs;

        var documentSources = context.Select((c, i) => new DocumentSource(i + 1, c.Label, c.Heading, c.Page, c.Content, c.TokenCount)).ToList();
        var knowledgeSources = knowledge.Select(s => s with { Number = documentSources.Count + s.Number }).ToList();

        var history = new ChatHistory(SystemPrompt);
        history.AddUserMessage(BuildUserMessage(question, documentSources, knowledgeSources));
        var settings = new OpenAIPromptExecutionSettings { ReasoningEffort = answerSettings.AnswerEffort };

        var answer = new StringBuilder();
        if (onDelta is null)
            answer.Append((await chat.GetChatMessageContentAsync(history, settings, cancellationToken: cancellationToken)).Content);
        else
            await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(history, settings, cancellationToken: cancellationToken))
            {
                if (string.IsNullOrEmpty(chunk.Content))
                    continue;
                if (answer.Length == 0)
                    logger.LogInformation("İlk token {FirstTokenMs} ms", sw.ElapsedMilliseconds);
                answer.Append(chunk.Content);
                await onDelta(chunk.Content);
            }

        logger.LogInformation("Belge sorusu: {Context} ({DocChunks} parça, {DocTokens} token), {KbSources} kanun kaynağı; belge {DocumentMs} ms, kanun araması {SearchMs} ms, cevap {AnswerMs} ms",
            whole ? "belgenin tamamı" : "en yakın parçalar", documentSources.Count, documentSources.Sum(s => s.TokenCount), knowledgeSources.Count,
            documentMs, searchMs, sw.ElapsedMilliseconds - documentMs - searchMs);

        // Sadece cevapta gerçekten atıf yapılanlar döner; LLM'in uydurduğu numaralar elenir.
        var text = answer.ToString().Trim() is { Length: > 0 } t ? t : throw new InvalidOperationException("Boş yanıt.");
        var cited = CitationMarker().Matches(text).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
        return new DocumentAnswer(text,
            documentSources.Where(s => cited.Contains(s.Number)).ToList(),
            knowledgeSources.Where(s => cited.Contains(s.Number)).ToList(), knowledgeSources);
    }

    // Sorgu = soru + en yakın belge parçası. Vektörü cache'lenmez: belge metni içerir (oturumluk veri, KVKK).
    private async Task<List<KnowledgeSource>> SearchKnowledgeAsync(string question, DocumentChunk? nearest, CancellationToken cancellationToken)
    {
        var query = nearest is null ? question : $"{question}\n{WithoutFileName(nearest.Content)}";
        var vector = await embeddings.GenerateVectorAsync(query, cancellationToken: cancellationToken);
        // Madde atfı ("madde 5") kanun adı anılmadıkça belgenin maddesidir: kanundan doğrudan madde getirilmez.
        var routed = QueryRouter.Parse(question).Any(r => r.Law is not null) ? question : "";
        return await search.SearchAsync(routed, new Vector(vector), KnowledgeSearch.TokenBudget);
    }

    private static List<DocumentChunk> WithinBudget(List<DocumentChunk> ranked)
    {
        var context = new List<DocumentChunk>();
        var used = 0;
        foreach (var chunk in ranked)
        {
            if (context.Count > 0 && used + chunk.TokenCount > DocumentTokenBudget)
                break;
            context.Add(chunk);
            used += chunk.TokenCount;
        }
        return context;
    }

    // Parça metninin ilk satırı dosya adı (bkz. DocumentChunker): kanun aramasında gürültü.
    private static string WithoutFileName(string content) =>
        content.IndexOf('\n') is var i and >= 0 ? content[(i + 1)..] : content;

    private static string BuildUserMessage(string question, List<DocumentSource> document, List<KnowledgeSource> knowledge)
    {
        var sb = new StringBuilder("Belge parçaları:\n\n");
        foreach (var s in document)
            sb.Append($"[{s.Number}] {s.Label} (sayfa {s.Page})\n{WithoutFileName(s.Content)}\n\n");
        sb.Append("Kanun kaynakları:\n\n");
        foreach (var s in knowledge)
            sb.Append($"[{s.Number}] {s.Label}\n{s.Content}\n\n");
        sb.Append($"Soru: {question}");
        return sb.ToString();
    }

    [GeneratedRegex(@"\[(\d+)\]")]
    private static partial Regex CitationMarker();
}
