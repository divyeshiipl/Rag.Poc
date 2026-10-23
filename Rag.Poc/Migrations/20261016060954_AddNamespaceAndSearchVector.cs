using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Rag.Poc.Migrations
{
    /// <inheritdoc />
    public partial class AddNamespaceAndSearchVector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExcelColumnAliases");

            migrationBuilder.DropTable(
                name: "ExcelRows");

            migrationBuilder.DropTable(
                name: "ExcelTables");

            migrationBuilder.AddColumn<string>(
                name: "Namespace",
                table: "DocumentChunks",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "SearchVector",
                table: "DocumentChunks",
                type: "tsvector",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_Embedding",
                table: "DocumentChunks",
                column: "Embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_Namespace",
                table: "DocumentChunks",
                column: "Namespace");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_SearchVector",
                table: "DocumentChunks",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DocumentChunks_Embedding",
                table: "DocumentChunks");

            migrationBuilder.DropIndex(
                name: "IX_DocumentChunks_Namespace",
                table: "DocumentChunks");

            migrationBuilder.DropIndex(
                name: "IX_DocumentChunks_SearchVector",
                table: "DocumentChunks");

            migrationBuilder.DropColumn(
                name: "Namespace",
                table: "DocumentChunks");

            migrationBuilder.DropColumn(
                name: "SearchVector",
                table: "DocumentChunks");

            migrationBuilder.CreateTable(
                name: "ExcelColumnAliases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Alias = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CanonicalName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TableId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExcelColumnAliases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExcelTables",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ColumnsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndColumnNumber = table.Column<int>(type: "integer", nullable: false),
                    EndRowNumber = table.Column<int>(type: "integer", nullable: true),
                    HeaderRowNumber = table.Column<int>(type: "integer", nullable: false),
                    SourceRange = table.Column<string>(type: "text", nullable: false),
                    StartColumnNumber = table.Column<int>(type: "integer", nullable: false),
                    TableName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    WorksheetName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExcelTables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExcelTables_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExcelRows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TableId = table.Column<Guid>(type: "uuid", nullable: false),
                    CellMetadataJson = table.Column<string>(type: "jsonb", nullable: false),
                    ExcelRowNumber = table.Column<int>(type: "integer", nullable: false),
                    ValuesJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExcelRows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExcelRows_ExcelTables_TableId",
                        column: x => x.TableId,
                        principalTable: "ExcelTables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExcelColumnAliases_TableId_Alias",
                table: "ExcelColumnAliases",
                columns: new[] { "TableId", "Alias" });

            migrationBuilder.CreateIndex(
                name: "IX_ExcelRows_TableId_ExcelRowNumber",
                table: "ExcelRows",
                columns: new[] { "TableId", "ExcelRowNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExcelTables_DocumentId",
                table: "ExcelTables",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ExcelTables_DocumentId_WorksheetName",
                table: "ExcelTables",
                columns: new[] { "DocumentId", "WorksheetName" });
        }
    }
}
