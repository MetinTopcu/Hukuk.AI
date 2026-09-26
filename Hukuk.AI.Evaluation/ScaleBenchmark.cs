using System.Diagnostics;
using Npgsql;
using Pgvector;
using Pgvector.Npgsql;

namespace Hukuk.AI.Evaluation;

// Veri büyüdükçe exact arama ile HNSW'nin hız ve recall farkı ("dotnet run -- bench").
// Ayrı bench_chunks tablosu (uygulama şemasının parçası değil, her çalıştırmada baştan kurulur).
// Sentetik vektör: iki gerçek D vektörünün rastgele ağırlıklı karışımı + küçük gürültü, normalize.
// Böylece dağılım gerçek chunk'lara benzer ama 219 noktanın kopyaları kadar sıkışık olmaz.
// Sorgular: eval setindeki soruların (Birlesik) vektörleri. Doğru cevap = exact top-k (indeks yokken ölçülür).
public static class ScaleBenchmark
{
    static readonly int[] Sizes = [10_000, 100_000, 500_000];
    static readonly int[] EfSearches = [40, 100, 200];
    const int K = 50;          // eval'deki fetchCount
    const int Warmup = 5;      // önbelleği ısıtmak için ölçülmeyen ilk sorgular
    const double Noise = 0.01; // boyut başına gürültü (normu ~0.39)

    public static async Task RunAsync(string connectionString, IReadOnlyList<Vector> queries)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        await using var dataSource = builder.Build();

        var bases = new List<float[]>();
        await using (var cmd = dataSource.CreateCommand("SELECT embedding FROM knowledge_chunks WHERE chunk_strategy = 'D_Hibrit'"))
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                bases.Add(reader.GetFieldValue<Vector>(0).ToArray());

        await ExecAsync(dataSource, "DROP TABLE IF EXISTS bench_chunks; CREATE TABLE bench_chunks (id bigint PRIMARY KEY, embedding vector(1536) NOT NULL);");

        Console.WriteLine($"\n== Ölçek testi: {queries.Count} sorgu, top-{K}, süreler ms (medyan / p95) ==");
        var rng = new Random(42);
        var count = 0;
        foreach (var size in Sizes)
        {
            // İndeks yokken ekle (hızlı) ve exact'i ölç; sonra indeksi baştan kur.
            await ExecAsync(dataSource, "DROP INDEX IF EXISTS ix_bench_chunks_embedding_hnsw;");
            var sw = Stopwatch.StartNew();
            await InsertAsync(dataSource, bases, rng, count, size);
            count = size;
            await ExecAsync(dataSource, "VACUUM ANALYZE bench_chunks;");
            Console.WriteLine($"\n-- {size:N0} satır (ekleme {sw.Elapsed.TotalSeconds:F0} sn, tablo {await SizeMbAsync(dataSource, "bench_chunks", total: true)} MB) --");

            var (exactIds, exactMs) = await SearchAllAsync(dataSource, queries, setup: null);
            Console.WriteLine($"{"Exact",-16}{Stats(exactMs),20}");

            // Paralel kurulum Docker'ın 64 MB shm'sine sığmadığı için tek işçi.
            sw.Restart();
            await ExecAsync(dataSource, """
                SET maintenance_work_mem = '2GB'; SET max_parallel_maintenance_workers = 0;
                CREATE INDEX ix_bench_chunks_embedding_hnsw ON bench_chunks
                    USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);
                """);
            Console.WriteLine($"HNSW kurulum {sw.Elapsed.TotalSeconds:F0} sn, indeks {await SizeMbAsync(dataSource, "ix_bench_chunks_embedding_hnsw", total: false)} MB, " +
                              $"planner kendiliğinden HNSW seçiyor mu: {(await PlannerUsesHnswAsync(dataSource, queries[0]) ? "evet" : "hayır")}");

            Console.WriteLine($"{"",-16}{"süre",20}{"recall@10",12}{"recall@50",12}");
            foreach (var ef in EfSearches)
            {
                // Ölçülenin HNSW olduğundan emin olmak için exact yolları kapatılır.
                var (ids, ms) = await SearchAllAsync(dataSource, queries, $"SET hnsw.ef_search = {ef}; SET enable_seqscan = off; SET enable_sort = off;");
                Console.WriteLine($"{"HNSW ef=" + ef,-16}{Stats(ms),20}{Recall(exactIds, ids, 10),12:F3}{Recall(exactIds, ids, K),12:F3}");
            }
        }
    }

    static async Task InsertAsync(NpgsqlDataSource dataSource, List<float[]> bases, Random rng, int from, int to)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var writer = await conn.BeginBinaryImportAsync("COPY bench_chunks (id, embedding) FROM STDIN (FORMAT BINARY)");
        var dims = bases[0].Length;
        var v = new float[dims];
        for (long id = from; id < to; id++)
        {
            var a = bases[rng.Next(bases.Count)];
            var b = bases[rng.Next(bases.Count)];
            var w = 0.5 + rng.NextDouble() * 0.5; // ağırlıklı karışım: a baskın
            double norm = 0;
            for (var i = 0; i < dims; i++)
            {
                var x = w * a[i] + (1 - w) * b[i] + Noise * Gaussian(rng);
                v[i] = (float)x;
                norm += x * x;
            }
            norm = Math.Sqrt(norm);
            for (var i = 0; i < dims; i++)
                v[i] = (float)(v[i] / norm);

            await writer.StartRowAsync();
            await writer.WriteAsync(id);
            await writer.WriteAsync(new Vector(v), "vector");
        }
        await writer.CompleteAsync();
    }

    // Her sorgu için top-K id'ler ve süre. setup: bağlantı ayarları (ef_search vb.).
    static async Task<(List<List<long>> ids, List<double> ms)> SearchAllAsync(NpgsqlDataSource dataSource, IReadOnlyList<Vector> queries, string? setup)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        if (setup is not null)
            await new NpgsqlCommand(setup, conn).ExecuteNonQueryAsync();

        var ids = new List<List<long>>();
        var ms = new List<double>();
        for (var i = 0; i < Warmup + queries.Count; i++)
        {
            var query = queries[i % queries.Count];
            var sw = Stopwatch.StartNew();
            await using var cmd = new NpgsqlCommand($"SELECT id FROM bench_chunks ORDER BY embedding <=> $1 LIMIT {K}", conn) { Parameters = { new() { Value = query } } };
            await using var reader = await cmd.ExecuteReaderAsync();
            var row = new List<long>();
            while (await reader.ReadAsync())
                row.Add(reader.GetInt64(0));
            sw.Stop();
            if (i < Warmup)
                continue;
            ids.Add(row);
            ms.Add(sw.Elapsed.TotalMilliseconds);
        }
        return (ids, ms);
    }

    static async Task<bool> PlannerUsesHnswAsync(NpgsqlDataSource dataSource, Vector query)
    {
        await using var cmd = dataSource.CreateCommand($"EXPLAIN SELECT id FROM bench_chunks ORDER BY embedding <=> $1 LIMIT {K}");
        cmd.Parameters.Add(new() { Value = query });
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            if (reader.GetString(0).Contains("ix_bench_chunks_embedding_hnsw"))
                return true;
        return false;
    }

    static double Recall(List<List<long>> exact, List<List<long>> approx, int k) =>
        exact.Zip(approx, (e, a) => (double)e.Take(k).Intersect(a.Take(k)).Count() / k).Average();

    static string Stats(List<double> ms)
    {
        var sorted = ms.Order().ToList();
        return $"{sorted[sorted.Count / 2]:F1} / {sorted[(int)(sorted.Count * 0.95)]:F1}";
    }

    static async Task<long> SizeMbAsync(NpgsqlDataSource dataSource, string relation, bool total)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {(total ? "pg_total_relation_size" : "pg_relation_size")}('{relation}')");
        return (long)(await cmd.ExecuteScalarAsync())! / 1024 / 1024;
    }

    static async Task ExecAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.CommandTimeout = 0; // indeks kurulumu dakikalar sürebilir
        await cmd.ExecuteNonQueryAsync();
    }

    // Box-Muller: standart normal dağılım
    static double Gaussian(Random rng) =>
        Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
}
