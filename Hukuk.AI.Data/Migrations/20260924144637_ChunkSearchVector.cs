using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Hukuk.AI.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChunkSearchVector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                table: "knowledge_chunks",
                type: "tsvector",
                nullable: false)
                .Annotation("Npgsql:TsVectorConfig", "turkish")
                .Annotation("Npgsql:TsVectorProperties", new[] { "content" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "search_vector",
                table: "knowledge_chunks");
        }
    }
}
