using System.Collections.Concurrent;

namespace Hukuk.AI.Retrieval;

// Cache stampede koruması (single-flight): aynı soru aynı anda birden çok kez gelirse sadece ilki (lider) LLM'e gider,
// diğerleri liderin sonucunu bekler. Semantic cache anahtarı birebir olmadığı için HybridCache'in coalescing'i burada
// işe yaramaz; ayrıca lider cevabı akış halinde (streaming) alırken bekleyenlere sadece bitmiş cevap verilebilir.
// Süreç içi (tek instance). Birden çok instance'ta Redis kilidi (SET NX PX) gerekir.
public class InflightAnswers
{
    private readonly ConcurrentDictionary<string, Task<LegalAnswer>> _tasks = new();

    // IsLeader true ise factory bu çağrıda başlatıldı; false ise çalışan işin Task'ı döner.
    public (Task<LegalAnswer> Task, bool IsLeader) GetOrStart(string key, Func<Task<LegalAnswer>> factory)
    {
        var tcs = new TaskCompletionSource<LegalAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = _tasks.GetOrAdd(key, tcs.Task);
        if (existing != tcs.Task)
            return (existing, false);

        _ = RunAsync();
        return (tcs.Task, true);

        async Task RunAsync()
        {
            try { tcs.SetResult(await factory()); }
            catch (Exception e) { tcs.SetException(e); }
            finally { _tasks.TryRemove(new KeyValuePair<string, Task<LegalAnswer>>(key, tcs.Task)); }
        }
    }
}
