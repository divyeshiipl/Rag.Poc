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

        _logger.LogInformation("RAG retrieved context for question: {Question}\n{Context}", question, context);

        var prompt = $"""
             You are a document question-answering assistant.
                 
             Answer the QUESTION using only the factual evidence in the CONTEXT.
                 
             Rules:
                1. Answer using only factual evidence in the CONTEXT.
                2. Read all context sections and identify the source most directly relevant to the question.
                3. Prefer explicit statements and table mappings over assumptions or general descriptions.
                4. Preserve table headers, category-to-value relationships, and exact source terminology.
                5. For a technology stack question, return the technologies and categories explicitly listed in the relevant technology stack table.
                6. For a specific architecture layer, return only technologies explicitly associated with that layer.
                7. Never infer technologies, frameworks, programming languages, cloud platforms, or numerical values.
                8. Combine relevant chunks when necessary to answer the question completely, but exclude unrelated information.
                9. If the context does not support the requested answer, respond exactly: "Not found in the provided documentation."
                10. Keep the answer concise and directly address the question.
                11. Do not combine facts from unrelated sections merely because they share keywords.
                12. Preserve the source document's explicit labels and their associated values.
                13. For questions asking to list multiple items, stages, requirements, features, or durations:
                    - Include all relevant items explicitly supported by the provided context.
                    - Review all relevant context before answering; do not stop after finding only a partial answer.
                    - Preserve each item's original label and its associated value.
                    - Combine relevant chunks when necessary, without mixing unrelated facts.
                    - Ensure all requested categories are covered using relevant evidence from the CONTEXT, including information from different documents when necessary.
                    - Include all requested categories, such as performance limits and capacity limits, when supported by the context.
                    - Do not invent, assume, or infer missing items or values.
                    - If only partial information is available, provide the supported items and clearly state that the remaining information was not found.
                14. For questions about the history, records, or transactions of a specific entity:
                     - Identify the exact entity using its name, ID, or identifier.
                     - Retrieve and combine all relevant records associated with that entity from the provided context.
                     - Include every supported record and preserve its date, type, cost, and other associated values.
                     - Do not treat missing records in one chunk as proof that no records exist in the remaining context.
                     - If no relevant records are present in the context, respond exactly: "Not found in the provided documentation."


         
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

        _logger.LogInformation(
            "Ollama timings: Total={Total}ms, Load={Load}ms, PromptEval={PromptEval}ms, Eval={Eval}ms, PromptTokens={PromptTokens}, GeneratedTokens={GeneratedTokens}",
            result.TotalDuration / 1_000_000,
            result.LoadDuration / 1_000_000,
            result.PromptEvalDuration / 1_000_000,
            result.EvalDuration / 1_000_000,
            result.PromptEvalCount,
            result.EvalCount);

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

    #endregion
}
