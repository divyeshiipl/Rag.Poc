using Rag.Poc.Models;
using System.Text;

namespace Rag.Poc.Services;

public sealed class OllamaLanguageModelService
    : ILanguageModelService
{
    private readonly HttpClient _httpClient;

    private const string Model = "llama3.2:latest";

    public OllamaLanguageModelService(
        HttpClient httpClient)
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
            You are an AI assistant answering questions about
            internal company documents.

            Answer the user's question using ONLY the provided context.

            Rules:
            - Do not use outside knowledge.
            - Do not invent facts.
            - If the context does not contain enough information,
              say that the available documents do not provide enough
              information to answer the question.
            - Give a clear and concise answer.
            - When possible, organize procedural instructions as
              numbered steps.
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
            stream = false
        };

        using var response =
            await _httpClient.PostAsJsonAsync(
                "/api/generate",
                request,
                cancellationToken);

        response.EnsureSuccessStatusCode();

        var result =
            await response.Content.ReadFromJsonAsync<OllamaResponse>(
                cancellationToken: cancellationToken);

        if (result == null ||
            string.IsNullOrWhiteSpace(result.Response))
        {
            throw new InvalidOperationException(
                "Ollama returned an empty response.");
        }

        return result.Response.Trim();
    }

    private static string BuildContext(
        IReadOnlyList<DocumentChunk> chunks)
    {
        var builder = new StringBuilder();

        foreach (var chunk in chunks)
        {
            builder.AppendLine(
                $"[Section: {chunk.SectionTitle ?? "Unknown"}]");

            builder.AppendLine(chunk.Content);

            builder.AppendLine();
            builder.AppendLine("---");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private sealed class OllamaResponse
    {
        public string Response { get; set; } = string.Empty;
    }
}