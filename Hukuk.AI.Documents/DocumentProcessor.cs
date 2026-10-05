using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Hukuk.AI.Documents;

// Kuyruktan alınan tek belgenin işlenmesi: OCR (Markdown) → parçalama → embedding → Redis → risk raporu.
// Her aşamanın süresi loglanır.
// Hata kullanıcıya "failed" durumu olarak döner; iş tekrar denenmez (OCR ücretli, aynı dosya büyük ihtimalle yine düşer).
public class DocumentProcessor(DocumentStore store, IDocumentReader reader, DocumentChunker chunker,
    IEmbeddingGenerator<string, Embedding<float>> embeddings, RiskReportService reports, ILogger<DocumentProcessor> logger)
{
    // Worker belgeyi işlerken çökerse iş tekrar alınır; aynı belge üst üste çökertiyorsa bırakılır.
    private const int MaxAttempts = 2;

    // Embedding isteği başına parça: 16 x 500 token = 8K token (S0 embedding TPM'i düşük, 429 riskini azaltır).
    private const int EmbeddingBatchSize = 16;

    public async Task ProcessAsync(string id, CancellationToken cancellationToken)
    {
        var document = await store.GetFileAsync(id);
        if (document is null)
        {
            logger.LogInformation("Belge {Id} yok (süresi dolmuş veya zaten işlenmiş), atlanıyor", id);
            return;
        }
        var (info, content) = document.Value;

        if (await store.IncrementAttemptsAsync(id) > MaxAttempts)
        {
            await store.SetStatusAsync(id, DocumentStatus.Failed, "Belge işlenemedi.");
            return;
        }
        await store.SetStatusAsync(id, DocumentStatus.Processing);

        var sw = Stopwatch.StartNew();
        try
        {
            var read = await reader.ReadAsync(content, info.ContentType, cancellationToken);
            var ocrMs = sw.ElapsedMilliseconds;
            if (read.PageCount > AzureDocumentReader.MaxPages)
            {
                await store.SetStatusAsync(id, DocumentStatus.Failed, $"Belge en fazla {AzureDocumentReader.MaxPages} sayfa olabilir.");
                return;
            }

            var chunks = chunker.Split(info.FileName, read.Markdown);
            if (chunks.Count == 0)
            {
                await store.SetStatusAsync(id, DocumentStatus.Failed, "Belgede okunabilir metin bulunamadı.");
                return;
            }

            var vectors = new List<float[]>(chunks.Count);
            foreach (var batch in chunks.Chunk(EmbeddingBatchSize))
            {
                var generated = await embeddings.GenerateAsync(batch.Select(c => c.Content), cancellationToken: cancellationToken);
                vectors.AddRange(generated.Select(e => e.Vector.ToArray()));
            }
            var embedMs = sw.ElapsedMilliseconds - ocrMs;

            await store.SaveChunksAsync(id, read.PageCount, chunks, vectors);

            await store.SetStatusAsync(id, DocumentStatus.Analyzing);
            var reportStart = sw.ElapsedMilliseconds;
            await store.SaveReportAsync(id, await reports.GenerateAsync(read.Markdown, cancellationToken: cancellationToken));
            var reportMs = sw.ElapsedMilliseconds - reportStart;

            await store.SetStatusAsync(id, DocumentStatus.Ready);
            logger.LogInformation("Belge {Id} hazır: {Pages} sayfa, {Chunks} parça, {Tokens} token; OCR {OcrMs} ms, embedding {EmbedMs} ms, rapor {ReportMs} ms, toplam {TotalMs} ms",
                id, read.PageCount, chunks.Count, chunks.Sum(c => c.TokenCount), ocrMs, embedMs, reportMs, sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Belge {Id} işlenemedi", id);
            await store.SetStatusAsync(id, DocumentStatus.Failed, "Belge işlenemedi.");
        }
    }
}
