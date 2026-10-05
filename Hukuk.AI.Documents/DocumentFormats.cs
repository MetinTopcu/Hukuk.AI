namespace Hukuk.AI.Documents;

// Kabul edilen dosya türleri: Document Intelligence prebuilt-layout'un okuyabildiklerinin hukuk belgesi için anlamlı
// olanları (PDF: metin veya taranmış, Word, fotoğraf/tarama görüntüsü). Uzantıya güvenilmez; dosyanın ilk baytları
// (imza) uzantıyla uyuşmalı, yoksa reddedilir (yeniden adlandırılmış .exe OCR'a, yani paraya gitmesin).
public static class DocumentFormats
{
    private record Format(string ContentType, byte[][] Signatures);

    private static readonly Dictionary<string, Format> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = new("application/pdf", ["%PDF"u8.ToArray()]),
        [".docx"] = new("application/vnd.openxmlformats-officedocument.wordprocessingml.document", [[0x50, 0x4B, 0x03, 0x04]]), // zip
        [".jpg"] = new("image/jpeg", [[0xFF, 0xD8, 0xFF]]),
        [".jpeg"] = new("image/jpeg", [[0xFF, 0xD8, 0xFF]]),
        [".png"] = new("image/png", [[0x89, 0x50, 0x4E, 0x47]]),
        [".tif"] = new("image/tiff", [[0x49, 0x49, 0x2A, 0x00], [0x4D, 0x4D, 0x00, 0x2A]]),
        [".tiff"] = new("image/tiff", [[0x49, 0x49, 0x2A, 0x00], [0x4D, 0x4D, 0x00, 0x2A]]),
    };

    public static IReadOnlyCollection<string> Extensions => ByExtension.Keys;

    // Uzantı destekleniyor ve imza uyuşuyorsa content type, değilse null.
    public static string? Detect(string fileName, ReadOnlySpan<byte> content)
    {
        if (!ByExtension.TryGetValue(Path.GetExtension(fileName), out var format))
            return null;
        foreach (var signature in format.Signatures)
            if (content.StartsWith(signature))
                return format.ContentType;
        return null;
    }
}
