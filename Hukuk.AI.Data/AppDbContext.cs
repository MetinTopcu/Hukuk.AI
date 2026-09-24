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
            // İndeksler (B-tree, HNSW, GIN) ölçümlerden sonra eklenecek.
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
