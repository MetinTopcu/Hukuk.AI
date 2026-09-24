using NpgsqlTypes;
using Pgvector;

namespace Hukuk.AI.Data.Entities;

// Ortak hukuk bilgi tabanındaki (kanun maddeleri) tek bir parça ve vektörü.
public class KnowledgeChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Kanun numarası, örn: "6098", "4857"
    public string Law { get; set; } = string.Empty;

    // Örn: "Türk Borçlar Kanunu"
    public string LawName { get; set; } = string.Empty;

    // Chunk'ın içerdiği maddeler (jsonb). A/C/D'de tek madde; B'de pencere birden fazla maddeye yayılabilir.
    public List<ArticleRef> Articles { get; set; } = [];

    // B'de (çok maddeli pencere) boş. Kenar başlıkları hiyerarşisi, örn: "Konut ve Çatılı İşyeri Kiraları > E. Kira bedeli > II. Belirlenmesi"
    public string SectionPath { get; set; } = string.Empty;

    // Maddenin kendi kenar başlığı, örn: "Kiracının güvence vermesi"
    public string Heading { get; set; } = string.Empty;

    public ChunkStrategy ChunkStrategy { get; set; }

    // Bir madde birden fazla parçaya bölündüğünde sıra numarası (0'dan başlar)
    public int ChunkIndex { get; set; }

    public string Content { get; set; } = string.Empty;

    public int TokenCount { get; set; }

    // text-embedding-3-large, dimensions: 1536 (float32)
    public Vector? Embedding { get; set; }

    // Full-text arama için content'in kelime kökleri ('turkish' sözlüğü); Postgres üretir (generated column).
    public NpgsqlTsVector SearchVector { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Madde kimliği: Ek/Geçici maddeler asıl madde numaralarını tekrar kullandığı için tür de gerekli.
public record ArticleRef(ArticleType Type, int No);

public enum ArticleType
{
    Asil,
    Ek,
    Gecici
}

public enum ChunkStrategy
{
    A_Madde,
    B_SabitToken,
    C_MaddeBaglamli,
    D_Hibrit
}
