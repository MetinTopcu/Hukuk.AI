using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Hukuk.AI.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialKnowledgeChunks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "knowledge_chunks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    law = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    law_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    article_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    article_no = table.Column<int>(type: "integer", nullable: false),
                    section_path = table.Column<string>(type: "text", nullable: false),
                    heading = table.Column<string>(type: "text", nullable: false),
                    chunk_strategy = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    chunk_index = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    token_count = table.Column<int>(type: "integer", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(1536)", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_knowledge_chunks", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "knowledge_chunks");
        }
    }
}
