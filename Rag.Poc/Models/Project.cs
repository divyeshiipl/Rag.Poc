namespace Rag.Poc.Models;

public class Project
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string DocumentFolder { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public List<Document> Documents { get; set; } = [];
}
