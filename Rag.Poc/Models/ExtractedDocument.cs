namespace Rag.Poc.Models;

public class ExtractedDocument
{
    public string Text { get; set; } = string.Empty;

    public List<DocumentSection> Sections { get; set; } = [];
}
