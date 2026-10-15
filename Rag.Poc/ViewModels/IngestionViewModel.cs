namespace Rag.Poc.ViewModels;

public class IngestionViewModel
{
    public Guid? ProjectId { get; set; }
    public string? Message { get; set; }
    public bool? Success { get; set; }
    public IngestionResultViewModel? Result { get; set; }
}

public class IngestionResultViewModel
{
    public int NewDocuments { get; set; }
    public int ChangedDocuments { get; set; }
    public int UnchangedDocuments { get; set; }
    public int DeletedDocuments { get; set; }
    public int ProcessedDocuments { get; set; }
    public int FailedDocuments { get; set; }
}