namespace Rag.Poc.Models;

public class BgeSmallOptions
{
    public string ModelPath { get; set; } = string.Empty;

    public string VocabularyPath { get; set; } = string.Empty;

    public int MaxTokens { get; set; } = 512;
}
