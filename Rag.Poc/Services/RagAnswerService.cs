namespace Rag.Poc.Services;

/// <summary>
/// Generates RAG answers from retrieved chunks using the language model.
/// </summary>
public sealed class RagAnswerService
{
    private readonly DocumentSearchService _searchService;

    private readonly ILanguageModelService _languageModelService;

    /// <summary>
    /// Creates the service with the search and language model services.
    /// </summary>
    public RagAnswerService(DocumentSearchService searchService, ILanguageModelService languageModelService)
    {
        _searchService = searchService;
        _languageModelService = languageModelService;
    }

    #region Public Methods

    /// <summary>
    /// Answers the question from the given chunks, or returns a fallback message if there are none.
    /// </summary>
    public async Task<string> AnswerFromChunksAsync(string question, IReadOnlyList<DocumentChunk> chunks, IReadOnlyList<QueryFilter>? filters, CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0)
            return "I couldn't find relevant information in our company documents.";

        return await _languageModelService.GenerateAnswerAsync(question, chunks, filters, cancellationToken);
    }

    #endregion
}