namespace Rag.Poc.Constants;

public static class Namespaces
{
    public const string Default = "default";
    public const string Structural = "v2-structural";
    public const string Large = "v2-large";

    public static readonly IReadOnlyList<string> All =
        new[] { Default, Structural, Large };
}