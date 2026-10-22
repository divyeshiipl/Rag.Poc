using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Rag.Poc.Data;
using Rag.Poc.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Rag.Poc.Services;

public sealed class DocumentSearchService
{
    private readonly IDbContextFactory<RagDbContext> _dbFactory;
    private readonly IEmbeddingService _embeddingService;
    private readonly IRerankerService _reranker;
    private readonly ILogger<DocumentSearchService> _logger;

    private const int RrfK = 60;
    private const int MaxRerankCandidates = 20;

    public DocumentSearchService(
        IDbContextFactory<RagDbContext> dbFactory,
        IEmbeddingService embeddingService,
        IRerankerService reranker,
        ILogger<DocumentSearchService> logger)
    {
        _dbFactory = dbFactory;
        _embeddingService = embeddingService;
        _reranker = reranker;
        _logger = logger;
    }

    public async Task<List<ScoredChunk>> SearchAsync(
        RetrievalPlan plan,
        int? requestedTopK = null,
        string? requestedNamespace = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _logger.LogInformation(
            "Retrieval plan: Mode={Mode}, TopK={TopK}, Namespaces=[{Ns}], Filter={Filter}",
            plan.Mode, plan.TopK,
            plan.Namespaces is { Count: > 0 } ? string.Join(", ", plan.Namespaces) : "(none)",
            plan.FilterText);

        if (plan.Mode == RetrievalMode.Exhaustive && !string.IsNullOrWhiteSpace(plan.FilterText))
        {
            return await SearchAllByTextAsync(
                plan.FilterText, requestedNamespace, cancellationToken);
        }

        if (plan.Mode == RetrievalMode.Exhaustive)
        {
            _logger.LogWarning("Exhaustive without filterText; falling back to hybrid.");
        }

        // Narrow-subset questions (e.g. "what additional X beyond Y") benefit
        // from a wider net that the reranker trims back down.
        var effectiveTopK = requestedTopK ?? plan.TopK ?? 10;

        var namespaces = !string.IsNullOrWhiteSpace(requestedNamespace)
            ? new List<string> { requestedNamespace }
            : plan.Namespaces is { Count: > 0 }
                ? plan.Namespaces
                : new List<string> { Namespaces.Default };

        var all = new List<ScoredChunk>();
        foreach (var ns in namespaces)
        {
            var options = new RetrievalOptions
            {
                Query = string.IsNullOrWhiteSpace(plan.SearchQuery)
                    ? throw new ArgumentException("SearchQuery empty.", nameof(plan))
                    : plan.SearchQuery,
                TopK = effectiveTopK,
                Namespace = ns,
                UseHybrid = true,
                UseReranker = false
            };

            all.AddRange(await SearchDetailedAsync(options, cancellationToken));
        }

        // Cross-namespace dedupe by content hash (ChunkIndex differs across
        // namespaces because they use different chunk sizes).
        var deduped = all
            .GroupBy(x => ContentHash(x.Chunk.Content))
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(x => x.Score)
            .Take(effectiveTopK)
            .ToList();

        // Neighbour expansion: pull chunkIndex ± 1 for each result so the LLM
        // sees adjacent context. This is what rescues "answer is one chunk
        // past the hit" failures (very common with narrow-subset questions).
        var expanded = await ExpandNeighboursAsync(
            deduped, cancellationToken);

        return expanded;
    }

    // ============================================================
    // Neighbour expansion
    // ============================================================
    private async Task<List<ScoredChunk>> ExpandNeighboursAsync(
        List<ScoredChunk> results,
        CancellationToken cancellationToken)
    {
        if (results.Count == 0) return results;

        // Build a lookup of (documentId, namespace) → (minIdx, maxIdx).
        var spans = results
            .GroupBy(x => (x.Chunk.DocumentId, x.Chunk.Namespace))
            .ToDictionary(
                g => g.Key,
                g => (Min: g.Min(x => x.Chunk.ChunkIndex) - 1,
                      Max: g.Max(x => x.Chunk.ChunkIndex) + 1));

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var seen = new HashSet<Guid>(results.Select(r => r.Chunk.Id));

        // Fetch any (document, namespace, index-in-span) we don't already have.
        // SQL can't express "each group has different bounds" easily, so we do
        // a per-group query — the number of groups is tiny (documents × namespaces).
        foreach (var ((documentId, ns), (minIdx, maxIdx)) in spans)
        {
            var neighbourRows = await db.DocumentChunks
                .AsNoTracking()
                .Include(c => c.Document)
                .Where(c => c.DocumentId == documentId
                            && c.Namespace == ns
                            && c.ChunkIndex >= minIdx
                            && c.ChunkIndex <= maxIdx
                            && !seen.Contains(c.Id))
                .ToListAsync(cancellationToken);

            foreach (var row in neighbourRows)
            {
                seen.Add(row.Id);
                results.Add(new ScoredChunk
                {
                    Chunk = row,
                    // Neighbours rank just below their anchor so they don't
                    // displace higher-scoring primaries.
                    Score = 0.05,
                    Source = "neighbour"
                });
            }
        }

        return results;
    }

    private async Task<List<ScoredChunk>> SearchAllByTextAsync(
        string filterText,
        string? ns,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var q = db.DocumentChunks
            .AsNoTracking()
            .Include(c => c.Document)
            .Where(c => c.Content != null && c.Content.Contains(filterText));

        if (!string.IsNullOrWhiteSpace(ns))
            q = q.Where(c => c.Namespace == ns);

        var rows = await q
            .OrderBy(c => c.DocumentId)
            .ThenBy(c => c.ChunkIndex)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(c => ContentHash(c.Content))
            .Select(g => new ScoredChunk
            {
                Chunk = g.First(),
                Score = 1,
                Source = "exhaustive-text"
            })
            .ToList();
    }

    public async Task<List<ScoredChunk>> SearchWithScoresAsync(
        string query, int topK, string? ns,
        CancellationToken cancellationToken = default)
    {
        return await SearchDetailedAsync(new RetrievalOptions
        {
            Query = query,
            TopK = topK,
            Namespace = ns,
            UseHybrid = true,
            UseReranker = false
        }, cancellationToken);
    }

    public async Task<List<ScoredChunk>> SearchDetailedAsync(
        RetrievalOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Query))
            throw new ArgumentException("Query empty.", nameof(options));
        if (options.TopK <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.TopK));

        var candidates = options.UseHybrid
            ? options.TopK * options.CandidateMultiplier
            : options.TopK;

        var vectorTask = VectorSearchAsync(options, candidates, cancellationToken);
        var bm25Task = options.UseHybrid
            ? Bm25SearchAsync(options, candidates, cancellationToken)
            : Task.FromResult(new List<ScoredChunk>());

        await Task.WhenAll(vectorTask, bm25Task);

        var vectorResults = await vectorTask;
        var bm25Results = await bm25Task;

        List<ScoredChunk> merged = (options.UseHybrid && bm25Results.Count > 0)
            ? ReciprocalRankFusion(vectorResults, bm25Results)
            : vectorResults;

        if (options.MinSimilarity.HasValue)
        {
            merged = merged
                .Where(x => x.Source == "bm25" || x.Score >= options.MinSimilarity.Value)
                .ToList();
        }

        if (options.UseReranker && merged.Count > 1)
        {
            var toRerank = merged.Count > MaxRerankCandidates
                ? merged.Take(MaxRerankCandidates).ToList()
                : merged;

            var tail = merged.Count > MaxRerankCandidates
                ? merged.Skip(MaxRerankCandidates).ToList()
                : new List<ScoredChunk>();

            try
            {
                var reranked = await _reranker.RerankAsync(
                    options.Query, toRerank, options.TopK, cancellationToken);

                merged = reranked.Concat(tail).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reranker threw; using unranked order.");
            }
        }

        return merged.Take(options.TopK).ToList();
    }

    // ============================================================
    // Vector search — split on " | " so decomposed sub-queries
    // each get their own embedding (compound questions need this)
    // ============================================================
    private async Task<List<ScoredChunk>> VectorSearchAsync(
        RetrievalOptions options, int candidates,
        CancellationToken cancellationToken)
    {
        var subQueries = SplitSubQueries(options.Query);

        // Run all sub-query embeddings in parallel.
        var embeddingTasks = subQueries
            .Select(q => _embeddingService.GenerateQueryEmbeddingAsync(q, cancellationToken))
            .ToList();

        var embeddings = await Task.WhenAll(embeddingTasks);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var byChunk = new Dictionary<Guid, ScoredChunk>();

        for (int s = 0; s < subQueries.Count; s++)
        {
            var embedding = embeddings[s];
            var queryVector = new Vector(embedding);

            var q = BuildFilteredQuery(db, options).Where(x => x.Embedding != null);

            var raw = await q
                .OrderBy(x => x.Embedding!.CosineDistance(queryVector))
                // Fetch fewer per sub-query; the union + RRF handles merging.
                .Take(Math.Max(3, candidates / subQueries.Count + 1))
                .ToListAsync(cancellationToken);

            foreach (var chunk in raw)
            {
                double similarity = chunk.Embedding == null
                    ? 0
                    : 1.0 - CosineDistance(chunk.Embedding.ToArray(), embedding);

                // Keep the best similarity seen for this chunk across sub-queries.
                if (!byChunk.TryGetValue(chunk.Id, out var existing)
                    || similarity > existing.Score)
                {
                    byChunk[chunk.Id] = new ScoredChunk
                    {
                        Chunk = chunk,
                        Score = similarity,
                        Source = "vector"
                    };
                }
            }
        }

        return byChunk.Values
            .OrderByDescending(x => x.Score)
            .Take(candidates)
            .ToList();
    }

    private static List<string> SplitSubQueries(string query)
    {
        var parts = query
            .Split(" | ", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        return parts.Count == 0 ? new List<string> { query } : parts;
    }

    private async Task<List<ScoredChunk>> Bm25SearchAsync(
        RetrievalOptions options, int candidates,
        CancellationToken cancellationToken)
    {
        var sanitized = options.Query.Replace(" | ", " ");
        var tsQuery = BuildTsQuery(sanitized);
        if (string.IsNullOrWhiteSpace(tsQuery)) return new List<ScoredChunk>();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var parsedQuery = NpgsqlTsQuery.Parse(tsQuery);
        var q = BuildFilteredQuery(db, options);

        var raw = await q
            .Where(x => x.SearchVector != null && x.SearchVector!.Matches(parsedQuery))
            .OrderByDescending(x => x.SearchVector!.Rank(parsedQuery))
            .Take(candidates)
            .ToListAsync(cancellationToken);

        return raw.Select(x => new ScoredChunk
        {
            Chunk = x,
            Score = 0,
            Source = "bm25"
        }).ToList();
    }

    private static IQueryable<DocumentChunk> BuildFilteredQuery(
        RagDbContext db, RetrievalOptions options)
    {
        var q = db.DocumentChunks
            .AsNoTracking()
            .Include(x => x.Document)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(options.Namespace))
            q = q.Where(x => x.Namespace == options.Namespace);

        if (options.CategoryFilter is { Count: > 0 })
        {
            var clauses = options.CategoryFilter
                .Select(c => $"\"category\":\"{c}\"").ToList();
            q = q.Where(x => x.MetadataJson != null &&
                clauses.Any(c => x.MetadataJson!.Contains(c)));
        }

        if (!string.IsNullOrWhiteSpace(options.PolicyTypeFilter))
        {
            var needle = $"\"policyType\":\"{options.PolicyTypeFilter}\"";
            q = q.Where(x => x.MetadataJson != null && x.MetadataJson.Contains(needle));
        }

        return q;
    }

    private static List<ScoredChunk> ReciprocalRankFusion(
        List<ScoredChunk> listA, List<ScoredChunk> listB)
    {
        var scores = new Dictionary<Guid, double>();
        var lookup = new Dictionary<Guid, ScoredChunk>();
        var sources = new Dictionary<Guid, HashSet<string>>();

        void Accumulate(List<ScoredChunk> list, string source)
        {
            for (int rank = 0; rank < list.Count; rank++)
            {
                var id = list[rank].Chunk.Id;
                var c = 1.0 / (RrfK + rank + 1);
                if (!scores.ContainsKey(id))
                {
                    scores[id] = 0;
                    lookup[id] = list[rank];
                    sources[id] = new HashSet<string>();
                }
                scores[id] += c;
                sources[id].Add(source);
            }
        }

        Accumulate(listA, "vector");
        Accumulate(listB, "bm25");

        return scores
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new ScoredChunk
            {
                Chunk = lookup[kv.Key].Chunk,
                Score = kv.Value,
                Source = string.Join("+", sources[kv.Key])
            })
            .ToList();
    }

    private static double CosineDistance(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 1;
        return 1.0 - (dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }

    private static string BuildTsQuery(string query)
    {
        var tokens = Regex.Split(query.ToLowerInvariant(), @"\W+")
            .Where(t => t.Length >= 2)
            .Distinct()
            .ToList();

        if (tokens.Count == 0) return string.Empty;

        return string.Join(" | ",
            tokens.Select(t => t.Contains('-') ? $"\"{t}\"" : t));
    }

    private static string ContentHash(string? content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;

        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content.Trim()));
        return Convert.ToHexString(bytes);
    }
}