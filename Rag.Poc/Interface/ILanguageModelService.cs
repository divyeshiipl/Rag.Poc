namespace Rag.Poc.Interface;

public interface ILanguageModelService
{
    Task<string> GenerateAnswerAsync(
        string question,
        IReadOnlyList<DocumentChunk> chunks,
        IReadOnlyList<QueryFilter>? filters,
        CancellationToken cancellationToken = default);
}
