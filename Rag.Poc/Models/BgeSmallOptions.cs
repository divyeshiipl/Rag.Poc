namespace Rag.Poc.Models;

/// <summary>
/// Represents the options for configuring the BgeSmall embedding model.
/// </summary>
public class BgeSmallOptions
{
    public string ModelPath { get; set; } = string.Empty;

    public string VocabularyPath { get; set; } = string.Empty;

    public int MaxTokens { get; set; } = 512;
}
