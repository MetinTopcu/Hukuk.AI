using System.Text.Encodings.Web;
using System.Text.Json;
using Hukuk.AI.Retrieval;

namespace Hukuk.AI.Evaluation;

// LLM çıktısı her çağrıda değişebildiği için eval sorularının yeniden yazımları dosyada saklanır;
// ölçümler bu dosyayla tekrarlanabilir olur.
public class RewriteCache(QueryRewriter rewriter, string cachePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<Dictionary<string, string>> RewriteAllAsync(IEnumerable<EvalQuestion> questions)
    {
        var cache = File.Exists(cachePath)
            ? JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(cachePath))!
            : new CacheFile(QueryRewriter.PromptVersion, QueryRewriter.SystemPrompt, []);

        if (cache.PromptVersion != QueryRewriter.PromptVersion)
            throw new InvalidOperationException($"{cachePath} prompt v{cache.PromptVersion} ile üretilmiş; güncel v{QueryRewriter.PromptVersion}. Dosyayı silip yeniden üretin.");

        foreach (var q in questions.Where(q => !cache.Rewrites.ContainsKey(q.Id)))
        {
            cache.Rewrites[q.Id] = await rewriter.RewriteAsync(q.Question);
            Console.WriteLine($"  {q.Id}: {cache.Rewrites[q.Id]}");
            File.WriteAllText(cachePath, JsonSerializer.Serialize(cache, JsonOptions)); // yarıda kesilirse kaybolmasın
        }

        return cache.Rewrites;
    }

    // Alan adları mevcut rewrites-v1.json ile aynı kalmalı.
    private record CacheFile(int PromptVersion, string SystemPrompt, Dictionary<string, string> Rewrites);
}
