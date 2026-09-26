using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hukuk.AI.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChunkIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_knowledge_chunks_chunk_strategy_law",
                table: "knowledge_chunks",
                columns: new[] { "chunk_strategy", "law" });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_chunks_embedding_hnsw_d",
                table: "knowledge_chunks",
                column: "embedding",
                filter: "chunk_strategy = 'D_Hibrit'")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" })
                .Annotation("Npgsql:StorageParameter:ef_construction", 64)
                .Annotation("Npgsql:StorageParameter:m", 16);

            // Madde atfıyla getirme (articles @> '[...]'). jsonb_path_ops: sadece @> destekler ama daha küçük ve hızlı.
            migrationBuilder.Sql("CREATE INDEX ix_knowledge_chunks_articles_gin ON knowledge_chunks USING gin (articles jsonb_path_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX ix_knowledge_chunks_articles_gin;");

            migrationBuilder.DropIndex(
                name: "ix_knowledge_chunks_chunk_strategy_law",
                table: "knowledge_chunks");

            migrationBuilder.DropIndex(
                name: "ix_knowledge_chunks_embedding_hnsw_d",
                table: "knowledge_chunks");
        }
    }
}
