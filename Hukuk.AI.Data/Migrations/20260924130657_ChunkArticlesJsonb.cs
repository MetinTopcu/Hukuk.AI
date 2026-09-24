using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hukuk.AI.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChunkArticlesJsonb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "article_no",
                table: "knowledge_chunks");

            migrationBuilder.DropColumn(
                name: "article_type",
                table: "knowledge_chunks");

            migrationBuilder.AddColumn<string>(
                name: "articles",
                table: "knowledge_chunks",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "articles",
                table: "knowledge_chunks");

            migrationBuilder.AddColumn<int>(
                name: "article_no",
                table: "knowledge_chunks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "article_type",
                table: "knowledge_chunks",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");
        }
    }
}
