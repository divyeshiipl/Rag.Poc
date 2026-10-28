namespace Rag.Poc.Services;

/// <summary>
/// Generates answers by calling a local Ollama model with a grounded prompt built from document chunks.
/// </summary>
public sealed class OllamaLanguageModelService : ILanguageModelService
{
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Creates the service with an HTTP client pointing at the Ollama host.
    /// </summary>
    public OllamaLanguageModelService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    #region Public Methods

    /// <summary>
    /// Answers the question using only the given chunks as context. If filters are present, the prompt switches to strict row-copying mode.
    /// </summary>
    public async Task<string> GenerateAnswerAsync(string question, IReadOnlyList<DocumentChunk> chunks, IReadOnlyList<QueryFilter>? filters, CancellationToken cancellationToken = default)
    {
        var context = BuildContext(chunks);
        var filterSection = BuildFilterSection(filters);

        var prompt = $"""
            You are an AI assistant answering questions about internal company documents.

            Answer using ONLY the provided context. Do not use outside knowledge.
            Do not invent facts.

            {filterSection}

            CONTEXT RULES:
            - Read EVERY [Source N] block before answering.
            - If two blocks answer different parts of the question, combine them.
            - If a question asks "what are the N things" or "list all X", check EVERY
              block for additional items. Do not stop at the first block that has an
              answer.
            - If the context does not contain enough information, say exactly:
              "The available documents do not provide enough information to answer this question."

            CONTEXT:
            {context}

            USER QUESTION:
            {question}

            ANSWER:
            """;

        var request = new
        {
            model = RagConstants.Model,
            prompt,
            stream = false,
            keep_alive = "1h",
            think = false,
            options = new
            {
                temperature = 0,
                num_predict = RagConstants.NumPredict,
                num_ctx = RagConstants.NumCtx,
                num_thread = Math.Max(2, Environment.ProcessorCount / 2),
                num_batch = 128
            }
        };

        using var response = await _httpClient.PostAsJsonAsync("/api/generate", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(result?.Response))
            throw new InvalidOperationException("Ollama returned an empty response.");

        return result.Response.Trim();
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Builds the filter/instruction block; tells the model to copy each "Row N:" line verbatim and add a "Found N rows." line. Returns empty if no filters.
    /// </summary>
    private static string BuildFilterSection(IReadOnlyList<QueryFilter>? filters)
    {
        if (filters is not { Count: > 0 })
            return string.Empty;

        var filterDesc = string.Join(" AND ", filters.Select(f => f.Operator == "between"
                ? $"{f.Column} between \"{f.Value}\" and \"{f.UpperBound}\""
                : $"{f.Column} {f.Operator} \"{f.Value}\""));

        return $"""
        FILTER: {filterDesc}

        The context contains a block labeled "VERIFIED MATCHING ROWS".
        Every "Row N:" line inside that block is a verified match.

        OUTPUT — exactly this:
        1. Copy each "Row N:" line from the block, verbatim, one per output line.
        2. Do not skip any row.
        3. Do not output anything except the row lines and a final count line.
        4. The final line is: "Found N rows." where N = number of rows copied.

        Example for a block with 3 rows:
          Row 5: Asset ID: AST-1003 | ...
          Row 10: Asset ID: AST-1008 | ...
          Row 11: Asset ID: AST-1009 | ...
          Found 3 rows.
        """;
    }

    /// <summary>
    /// Builds numbered [Source N] blocks from the chunks, with per-type length caps and a total size limit.
    /// </summary>
    private static string BuildContext(IReadOnlyList<DocumentChunk> chunks)
    {
        var sb = new StringBuilder();
        int i = 1;

        foreach (var chunk in chunks)
        {
            if (sb.Length >= RagConstants.MaxTotalContextChars) break;

            var content = chunk.Content ?? string.Empty;
            var isExcel = chunk.SourceLocation?.StartsWith("Worksheet:", StringComparison.OrdinalIgnoreCase) == true;
            var isColumnIndex = chunk.MetadataJson?.Contains("\"RecordType\":\"ExcelColumn\"") == true;

            int cap = isColumnIndex
                ? int.MaxValue
                : isExcel ? 2_000
                : RagConstants.MaxCharsPerChunk;

            if (content.Length > cap) content = content[..cap];

            sb.AppendLine($"[Source {i++}]");
            sb.AppendLine($"Section: {chunk.SectionTitle ?? "Unknown"}");
            sb.AppendLine($"Document: {chunk.Document?.FileName ?? "Unknown"}");
            if (isExcel)
                sb.AppendLine($"Location: {chunk.SourceLocation}");
            sb.AppendLine(content);
            sb.AppendLine("---");
        }

        return sb.ToString();
    }

    #endregion
}