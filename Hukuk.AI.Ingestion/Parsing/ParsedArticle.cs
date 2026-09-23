using Hukuk.AI.Data.Entities;

namespace Hukuk.AI.Ingestion.Parsing;

// HTML'den ayıklanmış tek bir kanun maddesi (henüz parçalanmamış).
public record ParsedArticle(
    string Law,
    ArticleType ArticleType,
    int ArticleNo,
    // Bölüm/ayırım başlıkları + kenar başlıkları, örn: "Kira Sözleşmesi > Genel Hükümler > C. Kiraya verenin borçları > I. Teslim borcu"
    string SectionPath,
    // Maddenin kendi kenar başlığı, örn: "I. Teslim borcu"
    string Heading,
    // "MADDE N-" öneki çıkarılmış metin, fıkralar "\n" ile ayrılır
    string Content,
    // Kendi hükmü olmayan madde: metni sadece "(Mülga: ...)" veya "(... ilgili olup yerine işlenmiştir.)" notu
    bool HasNoText);
