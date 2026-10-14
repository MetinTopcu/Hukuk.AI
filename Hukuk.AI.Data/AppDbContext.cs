using Hukuk.AI.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Hukuk.AI.Data;

// IdentityUserContext: rol tabloları yok (rol kullanılmıyor), sadece kullanıcı tabloları.
public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityUserContext<AppUser, Guid>(options)
{
    public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<CreditTransaction> CreditTransactions => Set<CreditTransaction>();

    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<AppUser>(e =>
        {
            e.ToTable("users");
            e.Property(x => x.PrivacyVersion).HasMaxLength(20);
        });
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.Property(x => x.TokenHash).HasMaxLength(64); // SHA-256 hex
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CreditTransaction>(e =>
        {
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Operation).HasMaxLength(40);
            e.HasIndex(x => new { x.UserId, x.Id }); // kullanıcının hareketleri, yeniden eskiye
            // Unique index'ler NULL'ları çakışma saymaz: sadece dolu olan satırlar tekilleşir.
            e.HasIndex(x => x.RefundOfId).IsUnique();
            e.HasIndex(x => x.PaymentId).IsUnique();
            // Ledger silinmez: kullanıcı silinmek istenirse önce hareketleri ne olacak kararı verilmeli.
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Payment>(e =>
        {
            e.Property(x => x.Package).HasMaxLength(40);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Price).HasPrecision(10, 2);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ProviderToken).HasMaxLength(200);
            e.Property(x => x.ProviderPaymentId).HasMaxLength(100);
            e.HasIndex(x => x.ProviderToken).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

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
    // API, Ingestion ve Evaluation aynı ayarları kullansın diye tek yerde. Generic: hem elle kurulan
    // DbContextOptionsBuilder<AppDbContext> hem de DI'daki AddDbContext'in verdiği builder ile çalışır.
    public static TBuilder UseHukukAiPostgres<TBuilder>(this TBuilder builder, string connectionString)
        where TBuilder : DbContextOptionsBuilder
    {
        builder.UseNpgsql(connectionString, o => o.UseVector())
               .UseSnakeCaseNamingConvention();
        return builder;
    }
}
