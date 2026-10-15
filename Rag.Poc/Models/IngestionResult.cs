namespace Rag.Poc.Models;

public class IngestionResult
{
    public int NewDocuments { get; set; }

    public int ChangedDocuments { get; set; }

    public int UnchangedDocuments { get; set; }

    public int DeletedDocuments { get; set; }

    public int ProcessedDocuments { get; set; }

    public int FailedDocuments { get; set; }
}
