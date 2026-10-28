namespace Rag.Poc.Interface;

/// <summary>
/// Provides functionality to generate the answer from llm model
/// </summary>
public interface ILanguageModelService
{
    Task<string> GenerateAnswerAsync(string question, IReadOnlyList<DocumentChunk> chunks, IReadOnlyList<QueryFilter>? filters, CancellationToken cancellationToken = default);
}
