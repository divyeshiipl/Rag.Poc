namespace Rag.Poc.Models;

public class Document
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long FileSize { get; set; }

    public string FilePath { get; set; } = string.Empty;

    public string FileHash { get; set; } = string.Empty;

    public DateTime LastModifiedUtc { get; set; }

    public string Status { get; set; } = "Pending";

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? ProcessedAtUtc { get; set; }

    public Project? Project { get; set; }

    public List<DocumentChunk> Chunks { get; set; } = [];
    public string RelativePath { get; set; } = string.Empty;
}