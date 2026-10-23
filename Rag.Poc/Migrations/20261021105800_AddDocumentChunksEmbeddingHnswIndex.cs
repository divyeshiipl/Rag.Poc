using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rag.Poc.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentChunksEmbeddingHnswIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
            CREATE INDEX IF NOT EXISTS ix_document_chunks_embedding_hnsw
            ON "DocumentChunks"
            USING hnsw ("Embedding" vector_cosine_ops);
            """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
            DROP INDEX IF EXISTS ix_document_chunks_embedding_hnsw;
            """);
        }
    }
}
