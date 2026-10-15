using Rag.Poc.Models;

namespace Rag.Poc.Services;

public interface ILanguageModelService
{
    Task<string> GenerateAnswerAsync(
    string question,
    IReadOnlyList<DocumentChunk> chunks,
    CancellationToken cancellationToken = default);
}
