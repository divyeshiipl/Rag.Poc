namespace Rag.Poc.Models;

public class DocumentSection
{
    public string? Heading { get; set; }

    public string Content { get; set; } = string.Empty;

    public string? SourceLocation { get; set; }

    public string? ContentType { get; set; }

    public Dictionary<string, string> Metadata { get; set; } = [];
}
