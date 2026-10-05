using Azure;
using Azure.AI.DocumentIntelligence;

namespace Hukuk.AI.Documents;

// OCR/metin çıkarma sonucu: belge Markdown olarak (başlıklar, paragraflar, tablolar) ve okunan sayfa sayısı.
public record ReadResult(string Markdown, int PageCount);

public interface IDocumentReader
{
    Task<ReadResult> ReadAsync(byte[] content, string contentType, CancellationToken cancellationToken = default);
}

// Tüm türler tek yoldan (kararlar 2026-09-30): Document Intelligence prebuilt-layout, çıktı Markdown.
// Metin PDF'i de taranmış PDF de görüntü de Word de aynı şekilde okunur; başlık/madde yapısı parçalama için korunur.
// Ücret okunan sayfa başına: 30 sayfa sınırından fazlasını okuyup ödememek için PDF/TIFF'te en fazla MaxPages + 1
// sayfa istenir (+1: sınırı aştığını anlamak için). F0 katmanı her belgenin sadece ilk 2 sayfasını okur.
public class AzureDocumentReader(DocumentIntelligenceClient client) : IDocumentReader
{
    public const int MaxPages = 30;

    public async Task<ReadResult> ReadAsync(byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        var options = new AnalyzeDocumentOptions("prebuilt-layout", BinaryData.FromBytes(content))
        {
            OutputContentFormat = DocumentContentFormat.Markdown,
        };
        if (contentType is "application/pdf" or "image/tiff") // sayfa aralığı Word'de desteklenmiyor
            options.Pages = $"1-{MaxPages + 1}";

        var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, options, cancellationToken);
        return new ReadResult(operation.Value.Content, operation.Value.Pages.Count);
    }
}

// Document Intelligence anahtarı girilmemişse: API yine açılır, sadece belge işleme anlaşılır bir hatayla düşer.
public class NotConfiguredDocumentReader : IDocumentReader
{
    public Task<ReadResult> ReadAsync(byte[] content, string contentType, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Document Intelligence yapılandırılmamış (AI:DocIntelEndpoint / AI:DocIntelKey).");
}
