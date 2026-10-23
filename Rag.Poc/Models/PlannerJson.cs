using Rag.Poc.Enums;

namespace Rag.Poc.Models;

public sealed class PlannerJson
{
    public string? SearchQuery { get; set; }
    public int? TopK { get; set; }
    public string[]? Namespaces { get; set; }
    public RetrievalMode? Mode { get; set; }
    public string? FilterText { get; set; }
    public double? Confidence { get; set; }
    public List<FilterJson>? Filters { get; set; }
}

public sealed class FilterJson
{
    public string? Column { get; set; }
    public string? Operator { get; set; }
    public string? Value { get; set; }
    public string? UpperBound { get; set; }
}
