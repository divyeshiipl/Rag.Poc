namespace Rag.Poc.Interface;

/// <summary>
/// Provides functionality for generating embeddings for documents and queries.
/// </summary>
public interface IEmbeddingService
{
    Task<float[]> GenerateDocumentEmbeddingAsync(string text, CancellationToken cancellationToken = default);

    Task<float[]> GenerateQueryEmbeddingAsync(string text, CancellationToken cancellationToken = default);
}
