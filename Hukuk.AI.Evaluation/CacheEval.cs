using System.Diagnostics;
using System.Numerics.Tensors;
using System.Text.Encodings.Web;
using System.Text.Json;
using Hukuk.AI.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel.ChatCompletion;
using StackExchange.Redis;

namespace Hukuk.AI.Evaluation;

// "dotnet run -- cache": semantic cache benzerlik eşiği. Eval sorularının hepsi (canlı koddaki gibi FLOAT16 HNSW)
// geçici bir Redis index'ine cevap olarak yazılır, sonra cache-pairs-v1.json'daki sorular aranır:
//   paraphrase: aynı anlam -> kendi sorusu en yakın gelmeli ve eşiği geçmeli (hit oranı = kazanç)
//   tuzak: benzer kelimeler, farklı cevap -> eşiği geçmemeli (geçerse kullanıcıya YANLIŞ cevap = risk)
// Ayrıca eval setindeki farklı soruların birbirine en yüksek benzerliği (ek tuzak) ve FLOAT16'nın FLOAT32'ye göre kaybı.
// Sadece eşik güvenli çıkmadı (tuzaklar .946'ya kadar); aday eşiğini geçenler LLM doğrulayıcısından geçirilir.
public static class CacheEval
{
    private record PairsFile(int Version, string Note, List<PairItem> Items);
    private record PairItem(string Id, List<string> Paraphrases, List<string> Traps);
    private record Probe(string Kind, string BaseId, string Text, float[] Vector);

    private const double MinCandidate = 0.75; // doğrulayıcı bu benzerliğin üstündeki her denemede çalıştırılır

    public static async Task RunAsync(IConnectionMultiplexer redis, IEmbeddingGenerator<string, Embedding<float>> generator, IChatCompletionService chat,
        List<EvalQuestion> questions, string pairsPath, string resultsDir)
    {
        var pairs = JsonSerializer.Deserialize<PairsFile>(File.ReadAllText(pairsPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        // k23, k01'in günlük dille yazılmışı (aynı anlam): k01 paraphrase'i olarak aranır, index'e yazılmaz.
        var stored = questions.Where(q => q.Id != "k23").ToList();
        var storedVectors = await EmbedAsync(generator, stored.Select(q => q.Question));
        var probes = new List<Probe>();
        foreach (var kind in new[] { "paraphrase", "tuzak" })
        {
            var items = pairs.Items.SelectMany(p => (kind == "paraphrase" ? p.Paraphrases : p.Traps).Select(t => (p.Id, t))).ToList();
            var vectors = await EmbedAsync(generator, items.Select(i => i.t));
            probes.AddRange(items.Select((i, n) => new Probe(kind, i.Id, i.t, vectors[n])));
        }

        var verifier = new CacheMatchVerifier(chat);
        var cache = new SemanticAnswerCache(redis, verifier, new SemanticCacheOptions(0, "idx:answers-eval", "answer-eval:"), NullLogger<SemanticAnswerCache>.Instance);
        try { await cache.DropIndexAsync(); } catch (RedisServerException) { } // önceki yarım kalmış çalıştırma
        await cache.EnsureIndexAsync();
        for (var i = 0; i < stored.Count; i++)
            await cache.StoreAsync(stored[i].Question, storedVectors[i], new LegalAnswer(stored[i].Id, []));

        var byId = stored.Select((q, i) => (q.Id, v: storedVectors[i])).ToDictionary(x => x.Id, x => x.v);
        var results = new List<(Probe p, string nearestId, double redisSim, double fp32Sim)>();
        foreach (var p in probes)
        {
            var nearest = (await cache.NearestAsync(p.Vector))!.Value;
            var nearestId = JsonSerializer.Deserialize<LegalAnswer>(nearest.AnswerJson)!.Answer;
            results.Add((p, nearestId, nearest.Similarity, Cosine(p.Vector, byId[nearestId])));
        }
        await cache.DropIndexAsync();

        // Doğrulayıcı: cache'teki soru (en yakın kayıt) ile yeni soru aynı cevabı mı gerektirir?
        var textById = stored.ToDictionary(q => q.Id, q => q.Question);
        var verdicts = new Dictionary<Probe, (bool same, long ms)>();
        using (var gate = new SemaphoreSlim(3)) // chat TPM kotası düşük: 3 paralel + 429 retry
            await Task.WhenAll(results.Where(r => r.redisSim >= MinCandidate).Select(async r =>
            {
                await gate.WaitAsync();
                try
                {
                    var (same, ms) = await AnswerEval.WithRetryAsync(async () =>
                    {
                        var sw = Stopwatch.StartNew();
                        return (await verifier.IsSameAnswerAsync(textById[r.nearestId], r.p.Text), sw.ElapsedMilliseconds);
                    });
                    lock (verdicts) verdicts[r.p] = (same, ms);
                }
                finally { gate.Release(); }
            }));
        bool Accepted((Probe p, string nearestId, double redisSim, double fp32Sim) r) => verdicts.TryGetValue(r.p, out var v) && v.same;

        // Eval setindeki farklı sorular birbirinin cevabı olamaz: her sorunun en yakın başka soruya benzerliği.
        var crossMax = stored.Select((q, i) => (q.Id, sim: stored.Select((_, j) => j == i ? -1 : Cosine(storedVectors[i], storedVectors[j])).Max())).ToList();

        var para = results.Where(r => r.p.Kind == "paraphrase").ToList();
        var traps = results.Where(r => r.p.Kind == "tuzak").ToList();
        Console.WriteLine($"\n== Semantic cache eşiği: {stored.Count} kayıt, {para.Count} paraphrase, {traps.Count} tuzak, {crossMax.Count} farklı-soru ==");
        Console.WriteLine($"Paraphrase: en yakın kendi sorusu {para.Count(r => r.nearestId == r.p.BaseId)}/{para.Count}; " +
                          $"benzerlik min {para.Min(r => r.redisSim):F3} ort {para.Average(r => r.redisSim):F3} maks {para.Max(r => r.redisSim):F3}");
        Console.WriteLine($"Tuzak:      benzerlik min {traps.Min(r => r.redisSim):F3} ort {traps.Average(r => r.redisSim):F3} maks {traps.Max(r => r.redisSim):F3}");
        Console.WriteLine($"Farklı soru: en yakın başka soruya benzerlik maks {crossMax.Max(c => c.sim):F3}");
        Console.WriteLine($"FLOAT16 (Redis) - FLOAT32 benzerlik farkı: ort {results.Average(r => Math.Abs(r.redisSim - r.fp32Sim)):F5} maks {results.Max(r => Math.Abs(r.redisSim - r.fp32Sim)):F5}");

        Console.WriteLine($"\n{"Eşik",-6}{"Paraphrase hit",16}{"Yanlış soruya hit",19}{"Tuzak hit",11}{"Farklı soru hit",17}");
        for (var t = 0.80; t <= 0.985; t += 0.01)
        {
            var hit = para.Count(r => r.redisSim >= t && r.nearestId == r.p.BaseId);
            var wrong = para.Count(r => r.redisSim >= t && r.nearestId != r.p.BaseId);
            var trapHit = traps.Count(r => r.redisSim >= t);
            var cross = crossMax.Count(c => c.sim >= t);
            Console.WriteLine($"{t,-6:F2}{$"{hit}/{para.Count} ({(double)hit / para.Count:P0})",16}{wrong,19}{trapHit,11}{cross,17}");
        }

        Console.WriteLine($"\n== Aday eşiği + LLM doğrulayıcı (v{CacheMatchVerifier.PromptVersion}, minimal) ==");
        Console.WriteLine($"Doğrulayıcı süresi: ort {verdicts.Values.Average(v => v.ms):F0} ms, maks {verdicts.Values.Max(v => v.ms)} ms ({verdicts.Count} çağrı)");
        Console.WriteLine($"{"Aday",-6}{"Paraphrase hit",16}{"Yanlış soruya hit",19}{"Tuzak sızan",13}{"Doğrulayıcı çağrısı",21}");
        for (var t = MinCandidate; t <= 0.905; t += 0.01)
        {
            var hit = para.Count(r => r.redisSim >= t && r.nearestId == r.p.BaseId && Accepted(r));
            var wrong = para.Count(r => r.redisSim >= t && r.nearestId != r.p.BaseId && Accepted(r));
            var leaked = traps.Count(r => r.redisSim >= t && Accepted(r));
            var calls = results.Count(r => r.redisSim >= t);
            Console.WriteLine($"{t,-6:F2}{$"{hit}/{para.Count} ({(double)hit / para.Count:P0})",16}{wrong,19}{leaked,13}{calls,21}");
        }
        Console.WriteLine("Doğrulayıcının geçirdiği tuzaklar:");
        foreach (var r in traps.Where(Accepted).OrderByDescending(r => r.redisSim))
            Console.WriteLine($"  {r.redisSim:F3} [{r.nearestId}] {textById[r.nearestId]}  ->  {r.p.Text}");
        Console.WriteLine("Doğrulayıcının reddettiği paraphrase'ler (doğru soru bulunmuşken):");
        foreach (var r in para.Where(r => r.nearestId == r.p.BaseId && verdicts.ContainsKey(r.p) && !Accepted(r)).OrderByDescending(r => r.redisSim))
        {
            var (a, b) = (CacheMatchVerifier.Numbers(textById[r.nearestId]), CacheMatchVerifier.Numbers(r.p.Text));
            var by = a.SetEquals(b) ? "LLM" : $"sayı {{{string.Join(",", a)}}} / {{{string.Join(",", b)}}}";
            Console.WriteLine($"  {r.redisSim:F3} [{r.nearestId}] ({by}) {textById[r.nearestId]}  ->  {r.p.Text}");
        }

        Console.WriteLine("\nEn benzer 8 tuzak:");
        foreach (var r in traps.OrderByDescending(r => r.redisSim).Take(8))
            Console.WriteLine($"  {r.redisSim:F3} [{r.p.BaseId} -> en yakın {r.nearestId}] {r.p.Text}");
        Console.WriteLine("En az benzer 8 paraphrase:");
        foreach (var r in para.OrderBy(r => r.redisSim).Take(8))
            Console.WriteLine($"  {r.redisSim:F3} [{r.p.BaseId} -> en yakın {r.nearestId}] {r.p.Text}");

        Directory.CreateDirectory(resultsDir);
        var outPath = Path.Combine(resultsDir, $"cache-threshold-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(new
        {
            Probes = results.Select(r => new { r.p.Kind, r.p.BaseId, r.p.Text, NearestId = r.nearestId, RedisSimilarity = Math.Round(r.redisSim, 4), Fp32Similarity = Math.Round(r.fp32Sim, 4),
                VerifierSame = verdicts.TryGetValue(r.p, out var v) ? v.same : (bool?)null, VerifierMs = verdicts.TryGetValue(r.p, out var v2) ? v2.ms : (long?)null }),
            CrossQuestionMax = crossMax.Select(c => new { c.Id, Similarity = Math.Round(c.sim, 4) }),
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine($"\nDetaylar -> {outPath}");
    }

    private static async Task<List<float[]>> EmbedAsync(IEmbeddingGenerator<string, Embedding<float>> generator, IEnumerable<string> texts) =>
        (await generator.GenerateAsync(texts)).Select(e => e.Vector.ToArray()).ToList();

    private static double Cosine(float[] a, float[] b) => TensorPrimitives.CosineSimilarity(a, b);
}
