using Hukuk.AI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Hukuk.AI.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<KnowledgeChunk>(e =>
        {
            e.Property(x => x.Law).HasMaxLength(10);
            e.Property(x => x.LawName).HasMaxLength(200);
            e.OwnsMany(x => x.Articles, a =>
            {
                a.ToJson();
                a.Property(r => r.Type).HasConversion<string>();
            });
            e.Property(x => x.ChunkStrategy).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Embedding).HasColumnType("vector(1536)");
            e.HasGeneratedTsVectorColumn(x => x.SearchVector, "turkish", x => new { x.Content });

            // HNSW (yaklaşık en yakın komşu), sadece kazanan strateji D için (partial index).
            // Tüm tabloda tek indeks olsaydı HNSW ef_search kadar aday bulup strateji filtresini sonra uygulardı;
            // adayların ~¼'ü D olduğundan istenen sayıda sonuç dönmezdi. Sorgu WHERE'i bu filtreyle birebir eşleşmeli.
            e.HasIndex(x => x.Embedding)
                .HasDatabaseName("ix_knowledge_chunks_embedding_hnsw_d")
                .HasMethod("hnsw")
                .HasOperators("vector_cosine_ops")
                .HasStorageParameter("m", 16)
                .HasStorageParameter("ef_construction", 64)
                .HasFilter("chunk_strategy = 'D_Hibrit'");

            // Her sorgu stratejiye göre, isteğe bağlı olarak kanuna göre filtreliyor.
            e.HasIndex(x => new { x.ChunkStrategy, x.Law });
            // articles GIN indeksi (owned JSON olduğu için EF'te tanımlanamıyor) migration'da SQL ile.
        });
    }
}

public static class DbContextOptionsExtensions
{
    // API ve Ingestion aynı ayarları kullansın diye tek yerde
    public static DbContextOptionsBuilder<AppDbContext> UseHukukAiPostgres(this DbContextOptionsBuilder<AppDbContext> builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, o => o.UseVector())
               .UseSnakeCaseNamingConvention();
        return builder;
    }
}
