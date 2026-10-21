using System.Text.Json;
using Rag.Poc.Models;

namespace Rag.Poc.Services;

public sealed class OllamaRerankerService : IRerankerService
{
    private readonly HttpClient _http;
    private const string Model = "llama3.2:latest";

    public OllamaRerankerService(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<ScoredChunk>> RerankAsync(
        string query,
        List<ScoredChunk> candidates,
        int topN,
        CancellationToken cancellationToken = default)
    {
        // Build a compact ranking prompt. This is a "LLM-as-reranker" —
        // less accurate than a true cross-encoder, but fits your stack.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are a relevance ranker. Given a user question and a list of passages,");
        sb.AppendLine("return the passage indices ordered from MOST to LEAST relevant.");
        sb.AppendLine("Respond ONLY with a JSON array of indices, e.g. [3,0,5,1].");
        sb.AppendLine();
        sb.AppendLine($"QUESTION: {query}");
        sb.AppendLine();
        sb.AppendLine("PASSAGES:");
        for (int i = 0; i < candidates.Count; i++)
        {
            var text = candidates[i].Chunk.Content;
            if (text.Length > 500) text = text[..500] + "...";
            sb.AppendLine($"[{i}] {text}");
            sb.AppendLine();
        }
        sb.AppendLine("JSON ARRAY:");

        var request = new { model = Model, prompt = sb.ToString(), stream = false };

        try
        {
            using var resp = await _http.PostAsJsonAsync("/api/generate", request, cancellationToken);
            resp.EnsureSuccessStatusCode();

            var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var raw = doc.GetProperty("response").GetString() ?? "[]";

            var order = ParseIndexArray(raw);
            if (order.Count == 0)
                return candidates.Take(topN).ToList();

            var reranked = new List<ScoredChunk>();
            var seen = new HashSet<int>();
            foreach (var idx in order)
            {
                if (idx < 0 || idx >= candidates.Count || !seen.Add(idx)) continue;
                reranked.Add(new ScoredChunk
                {
                    Chunk = candidates[idx].Chunk,
                    Score = candidates.Count - reranked.Count, // synthetic rank score
                    Source = "rerank"
                });
            }

            // Append any missed candidates at the end
            for (int i = 0; i < candidates.Count; i++)
            {
                if (seen.Contains(i)) continue;
                reranked.Add(candidates[i]);
            }

            return reranked.Take(topN).ToList();
        }
        catch
        {
            // Fail-soft: return original order.
            return candidates.Take(topN).ToList();
        }
    }

    private static List<int> ParseIndexArray(string raw)
    {
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return new List<int>();

        try
        {
            return JsonSerializer.Deserialize<List<int>>(raw[start..(end + 1)])
                   ?? new List<int>();
        }
        catch
        {
            return new List<int>();
        }
    }
}