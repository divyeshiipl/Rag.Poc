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
             11. Read tables as structured data and preserve each row's category-to-value relationship.
             12. Do not infer or invent requirements, targets, metrics, thresholds, or numerical values. Report only values explicitly supported by the provided context. If the question asks for target performance requirements, look specifically for documented response-time targets for login, asset search, dashboard, and report generation. Do not substitute development quality metrics or invent throughput, error-rate, or database-latency targets. If the relevant targets are not present in the context, respond exactly: "Not found in the provided documentation."
             13. Do not assign values from other categories to the requested category, even if those values appear in the same table or document.
             14. When multiple categories are requested, answer each category separately using its corresponding source values.
             15. Include all relevant values explicitly listed for each requested category. Do not omit supported values or add unsupported ones.
             16. Treat table headers and row labels as authoritative when determining which values belong to each category.
             17. Do not interpret every technology mentioned in a document as belonging to the Backend category.
             18. If different sections use different classifications, preserve each section's original classification instead of merging the categories.
             19. When multiple documents provide relevant evidence, combine compatible facts without changing their original meaning or category assignments.
             20. If the retrieved context does not contain enough evidence to establish a requested category's values, state that the available context is insufficient for that part of the answer.
             21. Keep the answer focused on the requested categories and do not add unrelated technologies.
             22. Identify which retrieved document most directly answers the user's question before composing the answer.
             23. Prefer direct, question-relevant evidence over information that is merely related to the same topic.
             24. Do not merge separate lists from different documents unless the question explicitly requests a combined or comprehensive list.
             25. Include a detail only when it directly answers the question or is necessary to understand the answer.
             26. If one retrieved source directly answers the question, use that source as the primary evidence. Do not add unrelated lists from other sources.
             27. Never claim that the answer is comprehensive unless the source explicitly establishes that it is.
             28. Do not add an insufficient-information disclaimer when the available evidence answers the question.
             29. When a question asks for a specific technology, identify the exact technology value explicitly mapped to the requested category in the context.
             30. For authentication technology questions, do not substitute general authentication methods, authorization concepts, user roles, browser access, or session handling for an explicitly listed authentication technology.
             31. For logging technology questions, return the technologies explicitly mapped to the Logging layer or category. Do not substitute audit activities, logged events, monitoring capabilities, session timeout, or database logs.
             32. When the context contains a direct table row matching the requested category, prioritize that row over general descriptions or related information from other sections.
             33. Preserve the exact technology names and values as written in the source, including names containing multiple components separated by a slash.
             34. Do not infer a technology value from the system's features or functionality when an explicit value is available in the context.
             35. Before finalizing the answer, verify that each returned value is explicitly supported by the context and mapped to the category asked about.
             36. If an explicit matching category-to-value mapping exists in the context, answer with that value directly without an insufficient-information disclaimer.
             37. Do not include alternative technologies, examples, assumptions, or explanations unless they are explicitly supported by relevant context and necessary to answer the question.
             38. For database relationship questions, identify the primary key, foreign key, and referenced table exactly as described in the source. Do not reverse the relationship or confuse a record's identifier with the foreign key referencing another entity.
             39. Do not infer or invent numerical targets, performance benchmarks, throughput, error rates, or database response times. Use only explicitly stated evidence from the provided context. For performance questions, report each documented operation and its exact response-time target. Do not substitute bug ratios, implementation accuracy, or testing metrics for system response-time requirements. If the context does not contain the requested targets, respond exactly: "Not found in the provided documentation."
         
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
