using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Rag.Poc.Data;
using Rag.Poc.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rag.Poc.Services;

public sealed class DocumentSearchService
{
    private readonly RagDbContext _db;
    private readonly IEmbeddingService _embeddingService;
    private readonly IRerankerService _reranker;
    private readonly ILogger<DocumentSearchService> _logger;

    private const int RrfK = 60; // RRF smoothing constant (§4.4)

    public DocumentSearchService(
        RagDbContext db,
        IEmbeddingService embeddingService,
        IRerankerService reranker,
        ILogger<DocumentSearchService> logger)
    {
        _db = db;
        _embeddingService = embeddingService;
        _reranker = reranker;
        _logger = logger;
    }

    // ------------------------------------------------------------
    // Public entry point
    // ------------------------------------------------------------
    public async Task<List<DocumentChunk>> SearchAsync(
        string query,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        var results = await SearchDetailedAsync(new RetrievalOptions
        {
            Query = query,
            TopK = topK,
            UseHybrid = true,
            UseReranker = false
        }, cancellationToken);

        return results.Select(x => x.Chunk).ToList();
    }

    // Used by RagController.InspectRetrieval
    public async Task<List<ScoredChunk>> SearchWithScoresAsync(
        string query,
        int topK,
        string? ns,
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

    // ------------------------------------------------------------
    // Main pipeline: filter → vector + BM25 → RRF → optional rerank
    // ------------------------------------------------------------
    public async Task<List<ScoredChunk>> SearchDetailedAsync(
        RetrievalOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Query))
            throw new ArgumentException("Query cannot be empty.", nameof(options));

        if (options.TopK <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.TopK));

        var candidates = options.UseHybrid
            ? options.TopK * options.CandidateMultiplier
            : options.TopK;

        // -------- Stage 1a: semantic (vector) --------
        var vectorResults = await VectorSearchAsync(
            options, candidates, cancellationToken);

        // -------- Stage 1b: keyword (BM25-ish via ts_rank) --------
        var bm25Results = options.UseHybrid
            ? await Bm25SearchAsync(options, candidates, cancellationToken)
            : new List<ScoredChunk>();

        // -------- Stage 2: merge --------
        List<ScoredChunk> merged;
        if (options.UseHybrid && bm25Results.Count > 0)
        {
            merged = ReciprocalRankFusion(vectorResults, bm25Results);
        }
        else
        {
            merged = vectorResults;
        }

        // -------- Stage 2b: threshold on semantic score --------
        if (options.MinSimilarity.HasValue)
        {
            merged = merged
                .Where(x => x.Source == "bm25" || x.Score >= options.MinSimilarity.Value)
                .ToList();
        }

        // -------- Stage 3: rerank (optional) --------
        if (options.UseReranker && merged.Count > 0)
        {
            merged = await _reranker.RerankAsync(
                options.Query,
                merged,
                options.TopK,
                cancellationToken);
        }

        return merged.Take(options.TopK).ToList();
    }

    // ------------------------------------------------------------
    // Vector search with metadata filters
    // ------------------------------------------------------------
    private async Task<List<ScoredChunk>> VectorSearchAsync(
        RetrievalOptions options,
        int candidates,
        CancellationToken cancellationToken)
    {
        var embedding = await _embeddingService
            .GenerateQueryEmbeddingAsync(options.Query, cancellationToken);

        var queryVector = new Vector(embedding);

        var q = BuildFilteredQuery(options)
            .Where(x => x.Embedding != null);

        // We fetch candidates and then score in memory because
        // EF+pgvector doesn't easily project the distance.
        var raw = await q
            .OrderBy(x => x.Embedding!.CosineDistance(queryVector))
            .Take(candidates)
            .ToListAsync(cancellationToken);

        // Compute cosine similarity = 1 - distance for reporting.
        var results = new List<ScoredChunk>(raw.Count);
        foreach (var chunk in raw)
        {
            double similarity = 0;
            if (chunk.Embedding != null)
            {
                similarity = 1.0 - CosineDistance(chunk.Embedding.ToArray(), embedding);
            }

            results.Add(new ScoredChunk
            {
                Chunk = chunk,
                Score = similarity,
                Source = "vector"
            });
        }

        return results;
    }

    // ------------------------------------------------------------
    // BM25-ish lexical search using Postgres full text search
    // ------------------------------------------------------------
    private async Task<List<ScoredChunk>> Bm25SearchAsync(
        RetrievalOptions options,
        int candidates,
        CancellationToken cancellationToken)
    {
        var tsQuery = BuildTsQuery(options.Query);
        if (string.IsNullOrWhiteSpace(tsQuery))
            return new List<ScoredChunk>();

        var q = BuildFilteredQuery(options);

        var raw = await q
            .Where(x => x.SearchVector != null &&
                        x.SearchVector!.Matches(NpgsqlTsQuery.Parse(tsQuery)))
            .OrderByDescending(x => x.SearchVector!.Rank(NpgsqlTsQuery.Parse(tsQuery)))
            .Take(candidates)
            .ToListAsync(cancellationToken);

        return raw.Select(x => new ScoredChunk
        {
            Chunk = x,
            Score = 0, // rank is only used for ordering inside BM25 list
            Source = "bm25"
        }).ToList();
    }

    // ------------------------------------------------------------
    // Metadata filters (category, policyType, namespace)
    // ------------------------------------------------------------
    private IQueryable<DocumentChunk> BuildFilteredQuery(RetrievalOptions options)
    {
        var q = _db.DocumentChunks
            .AsNoTracking()
            .Include(x => x.Document)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(options.Namespace))
            q = q.Where(x => x.Namespace == options.Namespace);

        // Inclusive category filter (§3.3: category in ['electronics','general'])
        if (options.CategoryFilter is { Count: > 0 })
        {
            // We store metadata as JSON. Simple containment check works for
            // small collections; use jsonb operators if you migrate to jsonb.
            var clauses = options.CategoryFilter
                .Select(c => $"\"category\":\"{c}\"")
                .ToList();

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

    // ------------------------------------------------------------
    // Reciprocal Rank Fusion (§4.4)
    // ------------------------------------------------------------
    private static List<ScoredChunk> ReciprocalRankFusion(
        List<ScoredChunk> listA,
        List<ScoredChunk> listB)
    {
        var scores = new Dictionary<Guid, double>();
        var lookup = new Dictionary<Guid, ScoredChunk>();
        var sources = new Dictionary<Guid, HashSet<string>>();

        void Accumulate(List<ScoredChunk> list, string source)
        {
            for (int rank = 0; rank < list.Count; rank++)
            {
                var id = list[rank].Chunk.Id;
                var contribution = 1.0 / (RrfK + rank + 1);

                if (!scores.ContainsKey(id))
                {
                    scores[id] = 0;
                    lookup[id] = list[rank];
                    sources[id] = new HashSet<string>();
                }
                scores[id] += contribution;
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
                Score = kv.Value, // RRF score
                Source = string.Join("+", sources[kv.Key])
            })
            .ToList();
    }

    // ------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------
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
        // Very simple: join tokens with ' | ' (OR). Postgres parses it.
        var tokens = Regex.Split(query.ToLowerInvariant(), @"\W+")
            .Where(t => t.Length >= 2)
            .Distinct()
            .ToList();

        if (tokens.Count == 0) return string.Empty;

        // Exact-identifier tokens (AX-204) get quoted.
        return string.Join(" | ",
            tokens.Select(t => t.Contains('-') ? $"\"{t}\"" : t));
    }
}