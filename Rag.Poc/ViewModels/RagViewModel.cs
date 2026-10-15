namespace Rag.Poc.ViewModels;

public class RagViewModel
{
    public string Question { get; set; } = string.Empty;
    public string? Answer { get; set; }
    public List<RagSourceViewModel> Sources { get; set; } = [];
    public double? ExecutionTimeMinutes { get; set; }
}

public class RagSourceViewModel
{
    public Guid DocumentId { get; set; }
    public string? DocumentName { get; set; }
    public int ChunkIndex { get; set; }
    public string? Section { get; set; }
    public string? SourceLocation { get; set; }
}