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

    /// <summary>
    /// Generates an answer to a question using the RAG approach.
    /// </summary>
    /// <param name="question"></param>
    /// <param name="topK"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public async Task<string> AnswerAsync(string question, int topK = 5, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Question cannot be empty.", nameof(question));

        if (topK <= 0)
            throw new ArgumentOutOfRangeException(nameof(topK));

        var isBroadQuestion = Regex.IsMatch(
            question,
            @"\b(all|every|complete|entire|list|summarize|summary|steps|phases|relationships|compare|across|features|changes|technology|technologies|stack|backend|frontend|
                database|databases|layer|layers|roles|targets|requirements|criteria|endpoints|primary keys|business rules|responsibilities|activities|reports|frequencies|statuses|priorities)\b",
            RegexOptions.IgnoreCase);

        var contextCount = isBroadQuestion
            ? Math.Clamp(topK * 2, 10, 20)
            : Math.Clamp(topK, 3, 8);

        var chunks = await _searchService.SearchAsync(question, contextCount, cancellationToken);

        if (chunks.Count == 0)
            return "I could not find relevant information in the provided documents.";

        return await _languageModelService.GenerateAnswerAsync(question, chunks, cancellationToken);
    }

}
