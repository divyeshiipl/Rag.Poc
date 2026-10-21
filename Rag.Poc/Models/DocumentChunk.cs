using Pgvector;

namespace Rag.Poc.Models;

public class DocumentChunk
{
    public Guid Id { get; set; }

    public Guid DocumentId { get; set; }

    public int ChunkIndex { get; set; }

    public string Content { get; set; } = string.Empty;

    public int TokenCount { get; set; }

    public string? SectionTitle { get; set; }

    public string? SourceLocation { get; set; }

    public string? MetadataJson { get; set; }

    public Vector? Embedding { get; set; }

    public Document? Document { get; set; }
    public string Namespace { get; set; } = "default";

    public NpgsqlTypes.NpgsqlTsVector? SearchVector { get; set; }
}
