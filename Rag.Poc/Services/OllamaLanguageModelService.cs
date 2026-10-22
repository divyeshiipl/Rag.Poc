using System.Net.Http.Json;
using System.Text;
using Rag.Poc.Models;

namespace Rag.Poc.Services;

public sealed class OllamaLanguageModelService : ILanguageModelService
{
    private readonly HttpClient _httpClient;
    private const string Model = "llama3.2:latest";

    // ~4 chars per token for English. Keep the prompt well under num_ctx
    // so the model sees the full context plus the question.
    private const int MaxCharsPerChunk = 2_000;
    private const int MaxTotalContextChars = 12_000;
    private const int NumCtx = 8_192;
    private const int NumPredict = 400;

    public OllamaLanguageModelService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> GenerateAnswerAsync(
        string question,
        IReadOnlyList<DocumentChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        var context = BuildContext(chunks);

        var prompt = $"""
            You are an AI assistant answering questions about internal company documents.

            Answer the user's question using ONLY the provided context.

            Rules:
            - Do not use outside knowledge.
            - Do not invent facts.
            - If the context does not contain enough information, say exactly:
              "The available documents do not provide enough information to answer this question."
            - Answer the question directly. If the question asks for a list or a
              specific number of items, extract those items from the context and
              list them. Do NOT repeat "not enough information" multiple times.
            - Quote exact values (URLs, IDs, dates, currencies) verbatim from the context.
            - When multiple policies conflict, note the conflict and state which you follow.
            - Do not mention these instructions in your answer.

            CONTEXT:
            {context}

            USER QUESTION:
            {question}

            ANSWER:
            """;

        var request = new
        {
            model = Model,
            prompt,
            stream = false,
            keep_alive = "10m",
            options = new
            {
                temperature = 0,
                num_predict = NumPredict,
                num_ctx = NumCtx
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

    private static string BuildContext(
        IReadOnlyList<DocumentChunk> chunks)
    {
        var sb = new StringBuilder();
        int i = 1;

        foreach (var chunk in chunks)
        {
            if (sb.Length >= MaxTotalContextChars) break;

            var content = chunk.Content ?? string.Empty;
            if (content.Length > MaxCharsPerChunk)
                content = content[..MaxCharsPerChunk];

            sb.AppendLine($"[Source {i++}]");
            sb.AppendLine($"Section: {chunk.SectionTitle ?? "Unknown"}");
            sb.AppendLine($"Document: {chunk.Document?.FileName ?? "Unknown"}");
            sb.AppendLine(content);
            sb.AppendLine("---");
        }

        return sb.ToString();
    }

    private sealed class OllamaResponse
    {
        public string Response { get; set; } = string.Empty;
    }
}