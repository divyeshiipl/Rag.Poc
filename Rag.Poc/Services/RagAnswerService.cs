namespace Rag.Poc.Services;

/// <summary>
/// Provides functionality for generating answers using the RAG approach.
/// </summary>
public sealed class RagAnswerService
{
    private readonly DocumentSearchService _searchService;

    private readonly ILanguageModelService _languageModelService;

    /// <summary>
    /// Initializes a new instance of the <see cref="RagAnswerService"/> class.
    /// </summary>
    /// <param name="searchService"></param>
    /// <param name="languageModelService"></param>
    public RagAnswerService(DocumentSearchService searchService, ILanguageModelService languageModelService)
    {
        _searchService = searchService;
        _languageModelService = languageModelService;
    }

    #region Public Methods
    /// <summary>
    /// Get the Answer from llm model
    /// </summary>
    /// <param name="question"></param>
    /// <param name="chunks"></param>
    /// <param name="filters"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<string> AnswerFromChunksAsync(string question, IReadOnlyList<DocumentChunk> chunks, IReadOnlyList<QueryFilter>? filters, CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0)
            return "I couldn't find relevant information in our company documents.";

        return await _languageModelService.GenerateAnswerAsync(question, chunks, filters, cancellationToken);
    }

    #endregion
}