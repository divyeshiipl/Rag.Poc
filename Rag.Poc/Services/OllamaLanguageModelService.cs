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

        var statusAnswer = TryBuildStatusAnswer(question, chunks);

        if (statusAnswer != null)
            return statusAnswer;

        var contextChunks = FilterContextForQuestion(question, chunks);

        _logger.LogInformation("Filtered Context: {@Chunks}", contextChunks.Select(x => new
        {
            x.Document?.FileName,
            x.ChunkIndex,
            x.SectionTitle,
            x.Content
        }));

        var context = BuildContext(contextChunks);

        _logger.LogInformation("Final Ollama Context:\n{Context}", context);

        _logger.LogInformation("RAG retrieved context for question: {Question}\n{Context}", question, context);

        var prompt = $"""
             You are a document question-answering assistant.
                 
             Answer the QUESTION using only the factual evidence in the CONTEXT.
                 
             Rules:
                1. Answer using explicit factual evidence from the relevant context. When the question asks for specific items, names, values, scripts, or identifiers, extract those exact items from the relevant evidence. Do not substitute related procedures, descriptions, or information from unrelated documents. If relevant evidence is available, do not return "Not found in the provided documentation."
                2. Identify the document, subject, entity, and information requested in the question. When the question specifies or targets a particular source document, prioritize evidence from that document. Do not use unrelated documents to answer the question. When multiple documents are provided, do not combine information from different documents unless the question explicitly requires comparison or cross-document analysis.
                3. Combine all relevant chunks when necessary to provide a complete answer. Include all explicitly documented information relevant to the question, even when it appears across different chunks, pages, or documents. Do not mix unrelated facts or omit supported details.
                4. For questions asking to list multiple items, stages, requirements, features, scripts, database changes, outputs, or durations:
                    - Include all relevant documented entries.
                    - Preserve each item's original label and associated values.
                    - Connect related information across chunks when the evidence supports the relationship.
                    - Cover all requested categories, including performance and capacity limits when documented.
                    - If information is partially available, provide the supported details and identify what is missing.
                5. If the CONTEXT does not contain evidence supporting the requested answer, respond exactly: "Not found in the provided documentation." Never claim information is missing when matching evidence is available.
                6. Keep answers concise and directly address the question. Do not generate sample code unless explicitly requested.
                7. For questions about a technology stack or architecture:
                    - Return only technologies explicitly listed in the relevant documentation.
                    - For a specific architecture layer, include only technologies associated with that layer.
                    - Never infer technologies, frameworks, programming languages, cloud platforms, or numerical values.
                8. For questions about processes, jobs, or operations, include all documented inputs, validations, processing steps, status updates, conditional branches, triggered jobs, retry operations, database updates, and API response details relevant to the question. Do not replace documented steps with a generic summary.
                9. For API-related questions:
                    - Identify the exact API operation and its source document.
                    - Follow the documented HTTP method, endpoint, request parameters, mandatory fields, response structure, codes, and messages.
                    - When multiple API specifications exist, prioritize the operation whose description, consumer, and provider match the question.
                    - Keep API-specific details separate. Never substitute details from another API or invent undocumented URLs, headers, authentication requirements, parameters, fields, or responses.
                    - If details are partially documented, provide the supported information and identify what is not specified.
                10. For connectivity, network, firewall, gateway, or endpoint questions:
                        - Extract the actual network configuration values from the supplied evidence, including IP addresses, URLs, ports, protocols, source servers, and destination servers.
                        - Do not return only a section heading or document title.
                        - Preserve the labels associated with each value, such as External MP, Internal MP, Source, and Destination.
                        - Include every relevant value found in the context.
                        - If a requested value is missing, explicitly state that it was not found. Never infer or invent it.
                        - Do not infer or invent missing configuration values.
                11. For questions about the history, records, or transactions of a specific entity:
                        - Identify the exact entity using its name, ID, or identifier.
                        - Combine all relevant records associated with that entity.
                        - Preserve dates, types, costs, and other associated values.
                        - Do not treat missing records in one chunk as proof that no records exist elsewhere in the CONTEXT.
                        - If no relevant records are available, respond exactly: "Not found in the provided documentation."
                12. When the CONTEXT contains an explicit definition, schema, table, or property list matching the requested topic, use it. Include associated properties, data types, mandatory/optional status, and descriptions when available. Do not report information as missing when it is present.
                13. For cross-reference questions, connect records, entities, and release versions only when retrieved evidence explicitly supports the relationship. Do not guess or duplicate relationships.
                14. Never infer business rules, notification triggers, or timing from API URLs, dependencies, field names, or related functionality. If the requested condition is not explicitly documented, state that it is not specified.
                15. When multiple documents contain relevant evidence for a general question, compare the relevant information. State common behavior only when supported by the documents, and distinguish document-specific examples, fields, messages, and requirements.
                16. If the provided context contains relevant information, answer using that evidence. Do not return "Not found in the provided documentation" when relevant facts are available. For questions about delivered features or iteration changes, combine all relevant feature descriptions, work item IDs, and implementation details across the provided chunks. If only part of the requested information is available, provide that part and clearly identify what is missing.

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
                num_predict = 512
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

    /// <summary>
    /// Filters the context chunks based on the question.
    /// </summary>
    /// <param name="question"></param>
    /// <param name="chunks"></param>
    /// <returns></returns>
    private static List<DocumentChunk> FilterContextForQuestion(string question, IReadOnlyList<DocumentChunk> chunks)
    {
        var operationNames = Regex.Matches(question, @"\b[A-Za-z0-9]+(?:-[A-Za-z0-9]+){2,}\b").Cast<Match>().Select(x => x.Value).ToArray();

        if (operationNames.Length > 0)
        {
            var operationChunks = chunks
                .Where(chunk => operationNames.Any(operation =>
                    chunk.Content.Contains(operation, StringComparison.OrdinalIgnoreCase) ||
                    (chunk.SectionTitle?.Contains(operation, StringComparison.OrdinalIgnoreCase) ?? false)))
                .ToList();

            if (operationChunks.Count > 0)
                return operationChunks;
        }

        var isConnectivityQuestion = Regex.IsMatch(question, @"\b(connectivity|network|firewall|whitelist|allowlist|gateway|proxy|server|endpoint|port|protocol)\b", RegexOptions.IgnoreCase);

        if (isConnectivityQuestion)
        {
            var queryTerms = Regex.Matches(question, @"\b[A-Za-z][A-Za-z0-9]{2,}\b")
                .Cast<Match>()
                .Select(x => x.Value)
                .Where(x => !Regex.IsMatch(
                    x,
                    @"^(which|what|where|when|does|with|from|required|need|needed)$",
                    RegexOptions.IgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var connectivityChunks = chunks
                .Where(chunk =>
                {
                    var content = chunk.Content ?? string.Empty;
                    var hasNetworkEvidence = Regex.IsMatch(content, @"\b(?:\d{1,3}\.){3}\d{1,3}\b|https?://|:\d{2,5}\b", RegexOptions.IgnoreCase);
                    var matchedTerms = queryTerms.Count(term => content.Contains(term, StringComparison.OrdinalIgnoreCase));

                    return hasNetworkEvidence && matchedTerms >= 2;
                })
                .ToList();

            if (connectivityChunks.Count > 0)
                return connectivityChunks;
        }

        var isDatabaseScriptQuestion = Regex.IsMatch(question, @"\b(database|db)\b", RegexOptions.IgnoreCase) && Regex.IsMatch(question, @"\b(script|scripts|deploy|deployment)\b", RegexOptions.IgnoreCase);

        if (isDatabaseScriptQuestion)
        {
            var queryTerms = Regex.Matches(
                    question,
                    @"\b[A-Za-z][A-Za-z0-9]{1,}\b")
                .Cast<Match>()
                .Select(x => x.Value)
                .Where(x => !Regex.IsMatch(
                    x,
                    @"^(which|what|where|when|does|do|the|is|are|for|from|with|database|db|script|scripts|deploy|deployment)$",
                    RegexOptions.IgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var databaseScriptChunks = chunks
                .Select(chunk =>
                {
                    var content = chunk.Content ?? string.Empty;
                    var fileName = chunk.Document?.FileName ?? string.Empty;
                    var section = chunk.SectionTitle ?? string.Empty;

                    var scriptMatches = Regex.Matches(content, @"\b\d+[_-](?:DB|Database)[A-Za-z0-9_-]*Script[A-Za-z0-9_-]*\b", RegexOptions.IgnoreCase).Count;
                    var searchableText = $"{fileName} {section} {content}";
                    var matchedTerms = queryTerms.Count(term => Regex.IsMatch(searchableText, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase));

                    return new
                    {
                        Chunk = chunk,
                        ScriptMatches = scriptMatches,
                        MatchedTerms = matchedTerms,
                        Score = (scriptMatches * 100) + (matchedTerms * 10)
                    };
                })
                .Where(x => x.ScriptMatches > 0 && x.MatchedTerms > 0)
                .OrderByDescending(x => x.MatchedTerms)
                .ThenByDescending(x => x.ScriptMatches)
                .ToList();

            return databaseScriptChunks.Select(x => x.Chunk).ToList();
        }

        var isDefinitionQuestion = Regex.IsMatch(question,
            @"\b(define|definition|meaning|what is|what does|how is .* identified|which values|allowed values|valid values|field requirements|conditional requirements)\b",
            RegexOptions.IgnoreCase);

        if (!isDefinitionQuestion)
            return chunks.ToList();

        return chunks.Where(x => !IsSampleJsonContent(x.Content)).ToList();
    }

    /// <summary>
    /// Determines if the content appears to be sample JSON.
    /// </summary>
    /// <param name="content"></param>
    /// <returns></returns>
    private static bool IsSampleJsonContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var jsonPropertyCount = Regex.Matches(content, @"""[A-Za-z][A-Za-z0-9_]*""\s*:").Count;

        return jsonPropertyCount >= 3;
    }


    /// <summary>
    /// Extracts status mappings from the provided document chunks.
    /// </summary>
    /// <param name="chunks"></param>
    /// <returns></returns>
    private static Dictionary<string, List<(string Code, string Description)>> ExtractStatusMappings(IReadOnlyList<DocumentChunk> chunks)
    {
        var result = new Dictionary<string, List<(string Code, string Description)>>(StringComparer.OrdinalIgnoreCase);
        var pattern = @"\b(?<field>[A-Za-z_][A-Za-z0-9_]*Status)\s*=\s*(?<code>\d+)\s*(?:\(\s*(?<description>[^)\r\n]+)\s*\))?";

        foreach (var chunk in chunks)
        {
            if (string.IsNullOrWhiteSpace(chunk.Content))
                continue;

            foreach (Match match in Regex.Matches(chunk.Content, pattern, RegexOptions.IgnoreCase))
            {
                var field = match.Groups["field"].Value;
                var code = match.Groups["code"].Value;
                var description = match.Groups["description"].Value.Trim();

                if (!result.TryGetValue(field, out var values))
                {
                    values = [];
                    result[field] = values;
                }

                var mapping = (code, description);

                if (!values.Contains(mapping))
                    values.Add(mapping);
            }
        }

        return result;
    }

    /// <summary>
    /// Tries to build a status answer based on the provided question and document chunks.
    /// </summary>
    /// <param name="question"></param>
    /// <param name="chunks"></param>
    /// <returns></returns>
    private static string? TryBuildStatusAnswer(string question, IReadOnlyList<DocumentChunk> chunks)
    {
        if (!Regex.IsMatch(question, @"\b(status|statuses)\b", RegexOptions.IgnoreCase))
            return null;

        var mappings = ExtractStatusMappings(chunks);

        if (mappings.Count == 0)
            return null;

        var builder = new StringBuilder();

        foreach (var field in mappings)
        {
            builder.AppendLine($"{field.Key}:");

            foreach (var item in field.Value)
            {
                var description = string.IsNullOrWhiteSpace(item.Description)
                    ? string.Empty
                    : $" - {item.Description}";

                builder.AppendLine($"- {item.Code}{description}");
            }

            builder.AppendLine();
        }

        return builder.ToString().Trim();
    }

    #endregion
}
