namespace Rag.Poc.Models;

public sealed class ScoredChunk
{
    public DocumentChunk Chunk { get; init; } = null!;
    public double Score { get; init; }
    public string Source { get; init; } = "vector"; // "vector" | "bm25" | "hybrid" | "rerank"
}