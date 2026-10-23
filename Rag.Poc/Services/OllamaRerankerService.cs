using System.Text;
using System.Text.Json;
using Rag.Poc.Constants;
using Rag.Poc.Models;

namespace Rag.Poc.Services;

public sealed class OllamaRerankerService : IRerankerService
{
    private readonly HttpClient _http;
    private readonly ILogger<OllamaRerankerService> _logger;

    public OllamaRerankerService(
        HttpClient http,
        ILogger<OllamaRerankerService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<List<ScoredChunk>> RerankAsync(
        string query,
        List<ScoredChunk> candidates,
        int topN,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count <= 1)
            return candidates.Take(topN).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("You are a relevance scorer. For each passage below, output a relevance score from 0.0 to 1.0.");
        sb.AppendLine("Output ONLY a JSON array of numbers, one per passage, in order. Example: [0.95, 0.12, 0.67]");
        sb.AppendLine();
        sb.AppendLine($"QUESTION: {query}");
        sb.AppendLine();
        sb.AppendLine("PASSAGES:");
        for (int i = 0; i < candidates.Count; i++)
        {
            var text = candidates[i].Chunk.Content ?? string.Empty;
            if (text.Length > RagConstants.MaxPassageChars)
                text = text[..RagConstants.MaxPassageChars] + "...";
            sb.AppendLine($"[{i}] {text}");
            sb.AppendLine();
        }
        sb.AppendLine("SCORES JSON ARRAY:");

        var request = new
        {
            model = RagConstants.RerankModel,
            prompt = sb.ToString(),
            stream = false,
            keep_alive = "10m",
            options = new
            {
                temperature = 0,
                num_predict = 128,
                num_ctx = 4_096
            }
        };

        try
        {
            using var resp = await _http.PostAsJsonAsync(
                "/api/generate", request, cancellationToken);

            resp.EnsureSuccessStatusCode();

            var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(
                cancellationToken);

            var raw = doc.TryGetProperty("response", out var respProp)
                ? respProp.GetString() ?? "[]"
                : "[]";

            var scores = ParseScoreArray(raw);

            if (scores.Count != candidates.Count)
            {
                _logger.LogWarning(
                    "Reranker returned {Returned} scores for {Expected} passages; falling back to original order.",
                    scores.Count, candidates.Count);
                return candidates.Take(topN).ToList();
            }

            return candidates
                .Select((c, i) => new ScoredChunk
                {
                    Chunk = c.Chunk,
                    Score = scores[i],
                    Source = "rerank"
                })
                .OrderByDescending(x => x.Score)
                .Take(topN)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Reranker call failed; using unranked order.");
            return candidates.Take(topN).ToList();
        }
    }

    private static List<double> ParseScoreArray(string raw)
    {
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return new List<double>();

        try
        {
            return JsonSerializer.Deserialize<List<double>>(
                       raw[start..(end + 1)])
                   ?? new List<double>();
        }
        catch
        {
            return new List<double>();
        }
    }
}