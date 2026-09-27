using System.Text.Json;
using Hukuk.AI.Data;
using Hukuk.AI.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Hukuk.AI.Retrieval;

public record RetrievedChunk(Guid Id, ArticleRef[] Articles, int TokenCount, double Similarity);

public record ChunkText(Guid Id, string Law, string LawName, ArticleRef[] Articles, string Heading, string Content);

// Exact (index'siz) cosine araması: ölçümlerin referans noktası. HNSW eklenince onun recall kaybı buna göre ölçülecek.
public class Retriever(AppDbContext db)
{
    public async Task<List<RetrievedChunk>> SearchAsync(Vector query, ChunkStrategy strategy, string? law, int k)
    {
        var chunks = db.KnowledgeChunks.AsNoTracking().Where(c => c.ChunkStrategy == strategy);
        if (law is not null)
            chunks = chunks.Where(c => c.Law == law); // metadata filtresi

        var rows = await chunks
            .OrderBy(c => c.Embedding!.CosineDistance(query))
            .Take(k)
            .Select(c => new { c.Id, c.Articles, c.TokenCount, Distance = c.Embedding!.CosineDistance(query) })
            .ToListAsync();

        return rows.Select(r => new RetrievedChunk(r.Id, [.. r.Articles], r.TokenCount, 1 - r.Distance)).ToList();
    }

    // HNSW (yaklaşık) arama, sadece D (partial index). WHERE koşulu indeks filtresiyle birebir aynı olmalı ki
    // planner indeksi seçebilsin. ef_search: aramada tutulan aday sayısı; k'dan küçükse en fazla ef_search sonuç döner.
    // Tablo küçük olduğu için planner exact taramayı (seq scan veya strateji B-tree'si + sort) daha ucuz bulur;
    // ölçümde HNSW'yi zorlamak için ikisi de kapatılır (HNSW sonucu zaten sıralı verdiği için sort gerekmez).
    // usedIndex: EXPLAIN planında HNSW indeksi görünüyor mu (ölçümün gerçekten indeksten geldiğinin kontrolü).
    public async Task<(List<Guid> ids, bool usedIndex)> HnswSearchAsync(Vector query, int k, int efSearch)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync($"SET LOCAL hnsw.ef_search = {efSearch}; SET LOCAL enable_seqscan = off; SET LOCAL enable_sort = off;");

        var plan = await db.Database.SqlQuery<string>($"""
            EXPLAIN SELECT id FROM knowledge_chunks WHERE chunk_strategy = 'D_Hibrit'
            ORDER BY embedding <=> {query} LIMIT {k}
            """).ToListAsync();

        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM knowledge_chunks WHERE chunk_strategy = 'D_Hibrit'
            ORDER BY embedding <=> {query} LIMIT {k}
            """).ToListAsync();

        await tx.CommitAsync();
        return (ids, plan.Any(line => line.Contains("ix_knowledge_chunks_embedding_hnsw_d")));
    }

    // BM25 kelime araması (Postgres yerleşik ts_rank_cd'de IDF yok; sıradan kelimeler ve uzun maddeler öne çıkıyordu).
    // Kökler 'turkish' sözlüğüyle çıkarılır (search_vector). Koleksiyon istatistikleri (N, df, ortalama uzunluk)
    // aynı strateji (ve varsa kanun filtresi) içindeki chunk'lardan hesaplanır. Similarity alanında BM25 puanı döner.
    public async Task<List<RetrievedChunk>> Bm25SearchAsync(string text, ChunkStrategy strategy, string? law, int k)
    {
        const double k1 = 1.2, b = 0.75; // literatürdeki standart değerler
        var strategyName = strategy.ToString();

        var scores = await db.Database.SqlQuery<Bm25Score>($"""
            WITH docs AS (
                SELECT id, search_vector FROM knowledge_chunks
                WHERE chunk_strategy = {strategyName} AND ({law}::text IS NULL OR law = {law})
            ),
            terms AS (   -- chunk başına kök ve geçme sayısı (tf)
                SELECT d.id, t.lexeme, array_length(t.positions, 1) AS tf
                FROM docs d, unnest(d.search_vector) t
            ),
            lengths AS (SELECT id, sum(tf) AS len FROM terms GROUP BY id),
            stats AS (SELECT count(*)::float8 AS n, avg(len)::float8 AS avgdl FROM lengths),
            query AS (SELECT DISTINCT unnest(tsvector_to_array(to_tsvector('turkish', {text}))) AS lexeme),
            df AS (   -- kökün kaç chunk'ta geçtiği
                SELECT t.lexeme, count(*)::float8 AS df FROM terms t JOIN query q ON q.lexeme = t.lexeme GROUP BY t.lexeme
            )
            SELECT t.id AS id,
                   sum(ln(1 + (s.n - df.df + 0.5) / (df.df + 0.5))                                   -- IDF: nadir kök daha değerli
                       * t.tf * ({k1} + 1) / (t.tf + {k1} * (1 - {b} + {b} * l.len / s.avgdl)))::float8 -- tf doygunluğu + uzunluk cezası
                   AS score
            FROM terms t
            JOIN df ON df.lexeme = t.lexeme
            JOIN lengths l ON l.id = t.id
            CROSS JOIN stats s
            GROUP BY t.id
            ORDER BY score DESC
            LIMIT {k}
            """).ToListAsync();

        var ids = scores.Select(x => x.Id).ToList();
        var chunks = await db.KnowledgeChunks.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Articles, c.TokenCount })
            .ToDictionaryAsync(c => c.Id);

        return scores.Select(x => new RetrievedChunk(x.Id, [.. chunks[x.Id].Articles], chunks[x.Id].TokenCount, x.Score)).ToList();
    }

    private class Bm25Score
    {
        public Guid Id { get; set; }
        public double Score { get; set; }
    }

    // Madde atfıyla doğrudan getirme (arama yok): articles jsonb'si bu maddeyi içeren chunk'lar, parça sırasıyla.
    public async Task<List<RetrievedChunk>> GetByArticlesAsync(IEnumerable<QueryRouter.LegalReference> references, ChunkStrategy strategy)
    {
        var strategyName = strategy.ToString();
        var result = new List<RetrievedChunk>();

        foreach (var r in references)
        {
            var match = JsonSerializer.Serialize(new[] { new { Type = r.Type.ToString(), r.No } });
            var ids = await db.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM knowledge_chunks
                WHERE chunk_strategy = {strategyName} AND ({r.Law}::text IS NULL OR law = {r.Law})
                  AND articles @> {match}::jsonb
                ORDER BY chunk_index
                """).ToListAsync();

            var chunks = await db.KnowledgeChunks.AsNoTracking()
                .Where(c => ids.Contains(c.Id))
                .Select(c => new { c.Id, c.Articles, c.TokenCount })
                .ToDictionaryAsync(c => c.Id);

            result.AddRange(ids.Select(id => new RetrievedChunk(id, [.. chunks[id].Articles], chunks[id].TokenCount, 1)));
        }

        return result.DistinctBy(c => c.Id).ToList();
    }

    // Seçilen chunk'ların metni ve künyesi, verilen sırayla (aramalar embedding'i taşımamak için sadece Id döndürüyor).
    public async Task<List<ChunkText>> GetTextsAsync(IReadOnlyList<Guid> ids)
    {
        var chunks = await db.KnowledgeChunks.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Law, c.LawName, c.Articles, c.Heading, c.Content })
            .ToDictionaryAsync(c => c.Id);

        return ids.Select(id => chunks[id])
            .Select(c => new ChunkText(c.Id, c.Law, c.LawName, [.. c.Articles], c.Heading, c.Content))
            .ToList();
    }

    // Ağırlıklı Reciprocal Rank Fusion: chunk puanı = Σ ağırlık / (k + sıra). k=60 literatürdeki standart değer.
    // Eşit ağırlıkta zayıf liste (BM25) güçlü listeyi (vektör) aşağı çekebildiği için ağırlık verilebilir.
    public static List<RetrievedChunk> FuseRrf(IEnumerable<(List<RetrievedChunk> ranking, double weight)> rankings, int k = 60) =>
        rankings
            .SelectMany(r => r.ranking.Select((chunk, index) => (chunk, score: r.weight / (k + index + 1))))
            .GroupBy(x => x.chunk.Id)
            .Select(g => (chunk: g.First().chunk, score: g.Sum(x => x.score)))
            .OrderByDescending(x => x.score)
            .Select(x => x.chunk)
            .ToList();
}
