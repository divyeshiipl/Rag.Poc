namespace Rag.Poc.Models;

public class SyncResult
{
    public List<Document> NewDocuments { get; } = [];

    public List<Document> ChangedDocuments { get; } = [];

    public List<Document> UnchangedDocuments { get; } = [];

    public List<Document> DeletedDocuments { get; } = [];
}
