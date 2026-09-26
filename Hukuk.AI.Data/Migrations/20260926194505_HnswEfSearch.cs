using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hukuk.AI.Data.Migrations
{
    /// <inheritdoc />
    public partial class HnswEfSearch : Migration
    {
        // HNSW en fazla ef_search kadar sonuç döndürür; 50 aday çektiğimiz için varsayılan 40 yetmiyor.
        // Ölçüm (eval "hnsw"): 40 -> recall@50 .800, 64 -> .998, 100 -> 1.000. Veritabanı düzeyinde: yeni bağlantılar alır.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN EXECUTE format('ALTER DATABASE %I SET hnsw.ef_search = 100', current_database()); END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN EXECUTE format('ALTER DATABASE %I RESET hnsw.ef_search', current_database()); END $$;");
        }
    }
}
