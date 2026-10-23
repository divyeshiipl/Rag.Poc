using Rag.Poc.Constants;

namespace Rag.Poc.Services;

public sealed class OllamaLanguageModelService : ILanguageModelService
{
    private readonly HttpClient _httpClient;

    public OllamaLanguageModelService(HttpClient httpClient)
    {
        _httpClient = httpClient;
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
    public async Task<string> GenerateAnswerAsync(
    string question,
    IReadOnlyList<DocumentChunk> chunks,
    CancellationToken cancellationToken = default)
    {
        var context = BuildContext(chunks);

        var parts = question.Split(" | ", StringSplitOptions.RemoveEmptyEntries);
        var multiNote = parts.Length > 1
            ? $"This query contains {parts.Length} sub-questions. Answer each one separately, " +
              $"using headings \"Answer 1:\", \"Answer 2:\", and so on."
            : string.Empty;

        var prompt = $"""
            You are an AI assistant answering questions about internal company documents.

            Answer using ONLY the provided context. Do not use outside knowledge.
            Do not invent facts.

            If the context does not contain enough information, say exactly:
            "The available documents do not provide enough information to answer this question."

            When the question is about rows in a spreadsheet:
            - The context may contain a chunk labelled "Column: X". That chunk is a
              complete list of Row N → value pairs for column X. Treat it as the
              full set of rows that exist in column X.
            - If the question filters on column X, list ONLY the rows whose value
              in column X matches the requested value. Rows absent from the column
              chunk must not be listed.
            - If a row block contains a mix of matching and non-matching rows,
              list only the matching rows.
            - Use the exact column names shown in the block: Asset ID, Asset Name,
              Category, Department, Location, Purchase Date, Purchase Cost ($),
              Status. Do not use placeholder names.
            - For count, sum, average, min, max, top N, bottom N questions, read
              the value from the "Aggregates" chunk. Do not compute manually.

            Format the answer as a plain list of rows, one row per line, using the
            text from the block. Do not explain your reasoning. Do not restate
            these instructions. Do not number your lines with "Step A", "Step B",
            etc. — just give the answer.

            {multiNote}

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
            keep_alive = "10m",
            think = false,
            options = new
            {
                temperature = 1.0,
                num_predict = RagConstants.NumPredict,
                num_ctx = RagConstants.NumCtx
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

    private static string BuildContext(IReadOnlyList<DocumentChunk> chunks)
    {
        var sb = new StringBuilder();
        int i = 1;

        foreach (var chunk in chunks)
        {
            if (sb.Length >= RagConstants.MaxTotalContextChars) break;

            var content = chunk.Content ?? string.Empty;
            var isExcel = chunk.SourceLocation?.StartsWith("Worksheet:", StringComparison.OrdinalIgnoreCase) == true;
            var cap = isExcel ? 6_000 : RagConstants.MaxCharsPerChunk;
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
}
