namespace Rag.Poc.Services;

/// <summary>
/// Provides language model functionality using the Ollama API.
/// </summary>
public sealed class OllamaLanguageModelService : ILanguageModelService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaLanguageModelService> _logger;
    private const string Model = "llama3.2:latest";

    /// <summary>
    /// Initializes a new instance of the <see cref="OllamaLanguageModelService"/> class.
    /// </summary>
    /// <param name="httpClient"></param>
    /// <param name="logger"></param>
    public OllamaLanguageModelService(HttpClient httpClient, ILogger<OllamaLanguageModelService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Generates an answer to a question based on the provided document chunks.
    /// </summary>
    /// <param name="question"></param>
    /// <param name="chunks"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public async Task<string> GenerateAnswerAsync(string question, IReadOnlyList<DocumentChunk> chunks, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Question cannot be empty.", nameof(question));

        if (chunks == null || chunks.Count == 0)
            return "The available documents do not provide enough information to answer the question.";

        var context = BuildContext(chunks);

        var prompt = $"""
             You are a document question-answering assistant.

             Answer the QUESTION using only the factual evidence in the CONTEXT.

             Rules:
             1. Read all context sections before answering.
             2. Determine which context sections are relevant to the question.
             3. Treat source document names, version numbers, and content as evidence
                for identifying the relevant information.
             4. Do not assume information is missing merely because the question
                uses different wording from the source.
             5. Combine all relevant facts from the context into one concise answer.
             6. Do not use unrelated documents to contradict relevant evidence.
             7. Do not invent facts or use outside knowledge.
             8. If the context supports only part of the answer, provide that part.
             9. State that the available documents do not provide enough information
                only when the context contains no evidence that answers the question.
             10. Do not mention these rules.

             CONTEXT:
             <context>
             {context}
             </context>

             QUESTION:
             {question}

             ANSWER:
             """;

        var request = new
        {
            model = Model,
            prompt,
            stream = false,
            options = new
            {
                temperature = 0,
                num_predict = 256
            }
        };

        using var response = await _httpClient.PostAsJsonAsync("/api/generate", request, cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Ollama request failed. Status: {StatusCode}. Body: {Body}", response.StatusCode, responseBody);
            response.EnsureSuccessStatusCode();
        }

        var result = System.Text.Json.JsonSerializer.Deserialize<OllamaResponse>(
            responseBody,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

        if (result == null || string.IsNullOrWhiteSpace(result.Response))
        {
            _logger.LogWarning("Ollama returned an empty answer for question: {Question}", question);
            throw new InvalidOperationException("Ollama returned an empty response.");
        }

        _logger.LogDebug("Ollama answer generated. Question: {Question}. Answer: {Answer}", question, result.Response);

        return result.Response.Trim();
    }

    #region Private Helper Methods

    /// <summary>
    /// Builds the context string from a list of document chunks.
    /// </summary>
    /// <param name="chunks"></param>
    /// <returns></returns>
    private static string BuildContext(IReadOnlyList<DocumentChunk> chunks)
    {
        var builder = new StringBuilder();

        foreach (var chunk in chunks)
        {
            builder.AppendLine($"Source document: {chunk.Document?.FileName ?? "Unknown"}");
            builder.AppendLine($"Chunk index: {chunk.ChunkIndex}");

            if (!string.IsNullOrWhiteSpace(chunk.SectionTitle))
                builder.AppendLine($"Section: {chunk.SectionTitle}");

            if (!string.IsNullOrWhiteSpace(chunk.SourceLocation))
                builder.AppendLine($"Source location: {chunk.SourceLocation}");

            builder.AppendLine("Content:");
            builder.AppendLine(chunk.Content);
            builder.AppendLine();
            builder.AppendLine("---");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// Represents the response from the Ollama API.
    /// </summary>
    private sealed class OllamaResponse
    {
        public string Response { get; set; } = string.Empty;
    }

    #endregion
}
