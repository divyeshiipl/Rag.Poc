namespace Rag.Poc.Interface;

/// <summary>
/// Provides the functionality of reranking of the chunks
/// </summary>
public interface IRerankerService
{
    Task<List<ScoredChunk>> RerankAsync(string query, List<ScoredChunk> candidates, int topN, CancellationToken cancellationToken = default);
}