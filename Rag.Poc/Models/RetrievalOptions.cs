namespace Rag.Poc.Models;

public sealed class RetrievalOptions
{
    public string Query { get; init; } = string.Empty;
    public int TopK { get; init; } = 5;
    public double? MinSimilarity { get; init; }         // §3.2
    public string? Namespace { get; init; }             // §2.7
    public List<string>? CategoryFilter { get; init; }  // §3.3
    public string? PolicyTypeFilter { get; init; }
    public bool UseHybrid { get; init; } = true;        // §4
    public bool UseReranker { get; init; } = false;     // §5
    public int CandidateMultiplier { get; init; } = 2;  // candidates = TopK * mult
}