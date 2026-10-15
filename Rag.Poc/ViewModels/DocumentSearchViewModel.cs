namespace Rag.Poc.ViewModels;

public class DocumentSearchViewModel
{
    public string Query { get; set; } = string.Empty;
    public List<DocumentSearchResultViewModel> Results { get; set; } = [];
}

public class DocumentSearchResultViewModel
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public int ChunkIndex { get; set; }
    public string? SectionTitle { get; set; }
    public string Content { get; set; } = string.Empty;
}