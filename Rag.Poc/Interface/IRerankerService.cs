using Rag.Poc.Models;

namespace Rag.Poc.Interface;

public interface IRerankerService
{
    Task<List<ScoredChunk>> RerankAsync(
        string query,
        List<ScoredChunk> candidates,
        int topN,
        CancellationToken cancellationToken = default);
}