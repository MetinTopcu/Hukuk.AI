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
            e.Property(x => x.ArticleType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ChunkStrategy).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Embedding).HasColumnType("vector(1536)");
            // İndeksler (B-tree, HNSW) ölçümlerden sonra eklenecek.
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
