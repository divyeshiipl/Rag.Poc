using Rag.Poc.Models;

namespace Rag.Poc.Services;

public sealed class RagAnswerService
{
    private readonly DocumentSearchService _searchService;
    private readonly ILanguageModelService _languageModelService;

    public RagAnswerService(
        DocumentSearchService searchService,
        ILanguageModelService languageModelService)
    {
        _searchService = searchService;
        _languageModelService = languageModelService;
    }

    //public async Task<string> AnswerAsync(
    //    string question,
    //    int topK = 5,
    //    CancellationToken cancellationToken = default)
    //{
    //    if (string.IsNullOrWhiteSpace(question))
    //        throw new ArgumentException("Question cannot be empty.", nameof(question));

    //    var chunks = await _searchService.SearchAsync(question, topK, cancellationToken);
    //    return await AnswerFromChunksAsync(question, chunks, cancellationToken);
    //}

    public async Task<string> AnswerFromChunksAsync(
        string question,
        IReadOnlyList<DocumentChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0)
            return "I couldn't find relevant information in our company documents.";

        return await _languageModelService.GenerateAnswerAsync(
            question, chunks, cancellationToken);
    }
}