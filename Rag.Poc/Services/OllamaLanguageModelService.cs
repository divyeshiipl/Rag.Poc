namespace Rag.Poc.Services;

public sealed class OllamaLanguageModelService : ILanguageModelService
{
    private readonly HttpClient _httpClient;

    public OllamaLanguageModelService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    #region Public Methods

    /// <summary>
    /// To Generate the answer from ollama with prompt 
    /// </summary>
    /// <param name="question"></param>
    /// <param name="chunks"></param>
    /// <param name="filters"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public async Task<string> GenerateAnswerAsync(
    string question,
    IReadOnlyList<DocumentChunk> chunks,
    IReadOnlyList<QueryFilter>? filters,
    CancellationToken cancellationToken = default)
    {
        var context = BuildContext(chunks);
        var filterSection = BuildFilterSection(filters);

        var prompt = $"""
        You are an AI assistant answering questions about internal company documents.

        Answer using ONLY the provided context. Do not use outside knowledge.
        Do not invent facts.

        If the context does not contain enough information, say exactly:
        "The available documents do not provide enough information to answer this question."

        {filterSection}

        CONTEXT:
        {context}

        USER QUESTION:
        {question}

        ANSWER:
        /no_think
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

        using var response = await _httpClient.PostAsJsonAsync(
            "/api/generate", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaResponse>(
            cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(result?.Response))
            throw new InvalidOperationException("Ollama returned an empty response.");

        return result.Response.Trim();
    }

    #endregion

    #region Private Methods
    private static string BuildFilterSection(IReadOnlyList<QueryFilter>? filters)
    {
        if (filters is not { Count: > 0 })
            return string.Empty;

        var filterDesc = string.Join(" AND ", filters.Select(f =>
            f.Operator == "between"
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

    private static string BuildContext(IReadOnlyList<DocumentChunk> chunks)
    {
        var sb = new StringBuilder();
        int i = 1;

        foreach (var chunk in chunks)
        {
            if (sb.Length >= RagConstants.MaxTotalContextChars) break;

            var content = chunk.Content ?? string.Empty;
            var isExcel = chunk.SourceLocation?.StartsWith(
                "Worksheet:", StringComparison.OrdinalIgnoreCase) == true;
            var cap = isExcel ? 2_000 : RagConstants.MaxCharsPerChunk;
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