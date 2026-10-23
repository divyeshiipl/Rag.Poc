using NpgsqlTypes;
using Rag.Poc.Constants;
using Rag.Poc.Enums;
using System.Security.Cryptography;

namespace Rag.Poc.Services;

public sealed class DocumentSearchService
{
    private readonly IDbContextFactory<RagDbContext> _dbFactory;
    private readonly IEmbeddingService _embeddingService;
    private readonly IRerankerService _reranker;
    private readonly ILogger<DocumentSearchService> _logger;

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
            "Retrieval plan: Mode={Mode}, TopK={TopK}, Namespaces=[{Ns}], Filters=[{Filters}]",
            plan.Mode, plan.TopK,
            plan.Namespaces is { Count: > 0 } ? string.Join(", ", plan.Namespaces) : "(none)",
            FormatFilters(plan.Filters));

        if (plan.Mode == RetrievalMode.Aggregate)
        {
            return await SearchAggregatesAsync(plan, requestedNamespace, cancellationToken);
        }

        if (plan.Mode == RetrievalMode.FullTable)
        {
            return await SearchFullTableAsync(plan, requestedNamespace, cancellationToken);
        }

        if (plan.Mode == RetrievalMode.Exhaustive
            && plan.Filters is { Count: > 0 })
        {
            var filtered = await SearchFilteredRowsAsync(plan, cancellationToken);
            if (filtered.Count > 0)
                return filtered;

            _logger.LogWarning(
                "Structured filter returned 0 rows ({FilterCount} filters); falling back.",
                plan.Filters.Count);
        }

        if (plan.Mode == RetrievalMode.Exhaustive
            && !string.IsNullOrWhiteSpace(plan.FilterText))
        {
            return await SearchAllByTextAsync(
                plan.FilterText, requestedNamespace, cancellationToken);
        }

        if (plan.Mode == RetrievalMode.Exhaustive)
        {
            _logger.LogWarning(
                "Exhaustive without filterText or structured filter; falling back to hybrid.");
        }

        var effectiveTopK = requestedTopK ?? plan.TopK ?? 10;

        var namespaces = !string.IsNullOrWhiteSpace(requestedNamespace)
            ? new List<string> { requestedNamespace }
            : plan.Namespaces is { Count: > 0 }
                ? plan.Namespaces
                : new List<string> { Namespaces.Default };

        var subQueries = plan.SearchQuery
            .Split(" | ", StringSplitOptions.RemoveEmptyEntries)
            .Select(q => q.Trim())
            .Where(q => q.Length > 0)
            .DefaultIfEmpty(plan.SearchQuery)
            .ToList();

        var perSubQuery = new List<(string SubQuery, List<ScoredChunk> Chunks)>();

        foreach (var sub in subQueries)
        {
            var results = new List<ScoredChunk>();
            foreach (var ns in namespaces)
            {
                var options = new RetrievalOptions
                {
                    Query = sub,
                    TopK = effectiveTopK,
                    Namespace = ns,
                    UseHybrid = true,
                    UseReranker = false
                };
                results.AddRange(await SearchDetailedAsync(options, cancellationToken));
            }
            perSubQuery.Add((sub, results));
        }

        var all = perSubQuery.SelectMany(x => x.Chunks).ToList();

        var docHints = ExtractDocumentHints(plan.SearchQuery);
        if (docHints.Count > 0)
        {
            all = all.Select(chunk =>
            {
                var docName = chunk.Chunk.Document?.FileName ?? "";
                if (docHints.Any(h => docName.Contains(h, StringComparison.OrdinalIgnoreCase)))
                {
                    return new ScoredChunk
                    {
                        Chunk = chunk.Chunk,
                        Score = chunk.Score + 0.15,
                        Source = chunk.Source,
                        SubQuery = chunk.SubQuery
                    };
                }
                return chunk;
            }).ToList();
        }

        var deduped = all
            .GroupBy(x => DedupeKey(x.Chunk))
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(x => x.Score)
            .Take(effectiveTopK * Math.Max(1, perSubQuery.Count))
            .ToList();

        var expanded = await ExpandNeighboursAsync(deduped, cancellationToken);

        var subQueryLookup = perSubQuery
            .SelectMany(p => p.Chunks.Select(c => (Id: c.Chunk.Id, p.SubQuery)))
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => g.First().SubQuery);

        return expanded
            .Select(c => subQueryLookup.TryGetValue(c.Chunk.Id, out var sub)
                ? new ScoredChunk
                {
                    Chunk = c.Chunk,
                    Score = c.Score,
                    Source = c.Source,
                    SubQuery = sub
                }
                : c)
            .ToList();
    }

    private static string FormatFilters(IReadOnlyList<QueryFilter>? filters)
    {
        if (filters is not { Count: > 0 }) return "(none)";
        return string.Join("; ", filters.Select(f =>
            $"{f.Column} {f.Operator} {f.Value}" +
            (f.UpperBound is null ? "" : $" (to {f.UpperBound})")));
    }

    private async Task<List<ScoredChunk>> ExpandNeighboursAsync(
        List<ScoredChunk> results,
        CancellationToken cancellationToken)
    {
        if (results.Count == 0) return results;

        var spans = results
            .Where(x => x.Chunk.MetadataJson?.Contains("\"RecordType\":\"ExcelRows\"") != true)
            .GroupBy(x => (x.Chunk.DocumentId, x.Chunk.Namespace))
            .ToDictionary(
                g => g.Key,
                g => (Min: g.Min(x => x.Chunk.ChunkIndex) - 1,
                      Max: g.Max(x => x.Chunk.ChunkIndex) + 1));

        if (spans.Count == 0) return results;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var seen = new HashSet<Guid>(results.Select(r => r.Chunk.Id));

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
                if (row.MetadataJson?.Contains("\"RecordType\":\"Excel") == true)
                    continue;

                seen.Add(row.Id);
                results.Add(new ScoredChunk
                {
                    Chunk = row,
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
            .Take(500)
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
            var toRerank = merged.Count > RagConstants.MaxRerankCandidates
                ? merged.Take(RagConstants.MaxRerankCandidates).ToList()
                : merged;

            var tail = merged.Count > RagConstants.MaxRerankCandidates
                ? merged.Skip(RagConstants.MaxRerankCandidates).ToList()
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

    private async Task<List<ScoredChunk>> VectorSearchAsync(
        RetrievalOptions options, int candidates,
        CancellationToken cancellationToken)
    {
        var subQueries = SplitSubQueries(options.Query);

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
                .Take(Math.Max(3, candidates / subQueries.Count + 1))
                .ToListAsync(cancellationToken);

            foreach (var chunk in raw)
            {
                double similarity = chunk.Embedding == null
                    ? 0
                    : 1.0 - CosineDistance(chunk.Embedding.ToArray(), embedding);

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
                var c = 1.0 / (RagConstants.RrfK + rank + 1);
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

    private async Task<List<ScoredChunk>> SearchAggregatesAsync(
        RetrievalPlan plan,
        string? ns,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var q = db.DocumentChunks
            .AsNoTracking()
            .Include(c => c.Document)
            .Where(c => c.MetadataJson != null &&
                        c.MetadataJson.Contains("\"RecordType\":\"ExcelAggregates\""));

        if (!string.IsNullOrWhiteSpace(ns))
            q = q.Where(c => c.Namespace == ns);
        else if (plan.Namespaces is { Count: > 0 })
            q = q.Where(c => plan.Namespaces.Contains(c.Namespace));

        var aggregates = await q.Take(200).ToListAsync(cancellationToken);

        var scored = aggregates.Select(c => new ScoredChunk
        {
            Chunk = c,
            Score = 1.0,
            Source = "aggregate"
        }).ToList();

        if (plan.Filters is { Count: > 0 })
        {
            scored.AddRange(await SearchFilteredRowsAsync(plan, cancellationToken));
        }

        return scored.Take(40).ToList();
    }

    private async Task<List<ScoredChunk>> SearchFullTableAsync(
        RetrievalPlan plan,
        string? ns,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var q = db.DocumentChunks
            .AsNoTracking()
            .Include(c => c.Document)
            .Where(c => c.MetadataJson != null &&
                        (c.MetadataJson.Contains("\"RecordType\":\"ExcelRows\"") ||
                         c.MetadataJson.Contains("\"RecordType\":\"ExcelWorksheet\"") ||
                         c.MetadataJson.Contains("\"RecordType\":\"ExcelColumn\"")));

        if (!string.IsNullOrWhiteSpace(ns))
            q = q.Where(c => c.Namespace == ns);
        else if (plan.Namespaces is { Count: > 0 })
            q = q.Where(c => plan.Namespaces.Contains(c.Namespace));

        var chunks = await q
            .OrderBy(c => c.DocumentId)
            .ThenBy(c => c.ChunkIndex)
            .Take(2000)
            .ToListAsync(cancellationToken);

        return chunks.Select(c => new ScoredChunk
        {
            Chunk = c,
            Score = 1.0,
            Source = "full-table"
        }).ToList();
    }

    private async Task<List<ScoredChunk>> SearchFilteredRowsAsync(
        RetrievalPlan plan,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var q = db.DocumentChunks
            .AsNoTracking()
            .Include(c => c.Document)
            .Where(c => c.MetadataJson != null &&
                        (c.MetadataJson.Contains("\"RecordType\":\"ExcelRows\"") ||
                         c.MetadataJson.Contains("\"RecordType\":\"ExcelColumn\"")));

        if (plan.Namespaces is { Count: > 0 })
            q = q.Where(c => plan.Namespaces.Contains(c.Namespace));

        var candidates = await q
            .OrderBy(c => c.DocumentId)
            .ThenBy(c => c.ChunkIndex)
            .Take(5000)
            .ToListAsync(cancellationToken);

        var results = new List<ScoredChunk>();
        var seenChunkIds = new HashSet<Guid>();

        foreach (var chunk in candidates)
        {
            if (chunk.MetadataJson?.Contains("\"RecordType\":\"ExcelRows\"") != true)
                continue;

            var matched = chunk.Content
                .Split('\n')
                .Any(line => line.StartsWith("Row ", StringComparison.OrdinalIgnoreCase)
                             && RowMatchesFilter(line, plan));

            if (matched && seenChunkIds.Add(chunk.Id))
            {
                results.Add(new ScoredChunk
                {
                    Chunk = chunk,
                    Score = 1.0,
                    Source = "filtered"
                });
            }
        }

        if (plan.Filters is { Count: > 0 })
        {
            foreach (var filter in plan.Filters)
            {
                if (string.IsNullOrWhiteSpace(filter.Column)) continue;

                var needle = $"\"ColumnName\":\"{filter.Column}\"";
                var columnChunks = candidates
                    .Where(c => c.MetadataJson?.Contains("\"RecordType\":\"ExcelColumn\"") == true
                                && c.MetadataJson.Contains(needle))
                    .ToList();

                foreach (var columnChunk in columnChunks)
                {
                    if (!seenChunkIds.Add(columnChunk.Id)) continue;
                    results.Add(new ScoredChunk
                    {
                        Chunk = columnChunk,
                        Score = 1.0,
                        Source = "filtered-column"
                    });
                }
            }
        }

        _logger.LogInformation(
            "SearchFilteredRowsAsync: {RowBlocks} row-blocks, {ColChunks} column-chunks, filters=[{Filters}]",
            results.Count(r => r.Source == "filtered"),
            results.Count(r => r.Source == "filtered-column"),
            FormatFilters(plan.Filters));

        results = results
            .OrderByDescending(r => r.Source == "filtered-column")
            .ThenByDescending(r => r.Score)
            .ToList();

        return results;
    }

    private static bool RowMatchesFilter(string rowLine, RetrievalPlan plan)
    {
        var colon = rowLine.IndexOf(':');
        if (colon < 0) return false;

        var fieldsPart = rowLine[(colon + 1)..];
        var fields = fieldsPart
            .Split('|')
            .Select(p => p.Trim())
            .Where(p => p.Contains(':'))
            .Select(p =>
            {
                var idx = p.IndexOf(':');
                return (Key: p[..idx].Trim(), Value: p[(idx + 1)..].Trim());
            })
            .ToList();

        if (plan.Filters is not { Count: > 0 })
            return true;

        foreach (var filter in plan.Filters)
        {
            var matchingFields = fields
                .Where(f => f.Key.Equals(filter.Column, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matchingFields.Count == 0)
                return false;

            var anyMatched = matchingFields.Any(f =>
                Matches(f.Value, filter.Operator, filter.Value, filter.UpperBound));

            if (!anyMatched)
                return false;
        }

        return true;
    }

    private static bool Matches(string cellValue, string op, string v1, string? v2)
    {
        if (string.IsNullOrWhiteSpace(cellValue)) return false;

        switch (op)
        {
            case "contains":
                return cellValue.Contains(v1, StringComparison.OrdinalIgnoreCase);
            case "=":
                return cellValue.Equals(v1, StringComparison.OrdinalIgnoreCase);
            case "!=":
                return !cellValue.Equals(v1, StringComparison.OrdinalIgnoreCase);
            case ">":
            case "<":
            case ">=":
            case "<=":
                {
                    if (TryCompareNumber(cellValue, v1, out var cmp)) return CompareResult(cmp, op);
                    if (TryCompareDate(cellValue, v1, out cmp)) return CompareResult(cmp, op);
                    return false;
                }
            case "between":
                {
                    if (v2 is null) return false;
                    if (TryCompareNumber(cellValue, v1, out var c1) &&
                        TryCompareNumber(cellValue, v2, out var c2))
                        return c1 >= 0 && c2 <= 0;
                    if (TryCompareDate(cellValue, v1, out c1) &&
                        TryCompareDate(cellValue, v2, out c2))
                        return c1 >= 0 && c2 <= 0;
                    return false;
                }
            default:
                return cellValue.Contains(v1, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool CompareResult(int comparison, string op) => op switch
    {
        ">" => comparison > 0,
        "<" => comparison < 0,
        ">=" => comparison >= 0,
        "<=" => comparison <= 0,
        _ => false
    };

    private static bool TryCompareNumber(string a, string b, out int comparison)
    {
        comparison = 0;
        a = a.Replace(",", "").Replace("$", "").Replace("€", "").Replace("£", "").Trim();
        b = b.Replace(",", "").Replace("$", "").Replace("€", "").Replace("£", "").Trim();

        if (decimal.TryParse(a, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var da) &&
            decimal.TryParse(b, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var db))
        {
            comparison = da.CompareTo(db);
            return true;
        }
        return false;
    }

    private static bool TryCompareDate(string a, string b, out int comparison)
    {
        comparison = 0;
        if (DateTime.TryParse(a, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var da) &&
            DateTime.TryParse(b, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var db))
        {
            comparison = da.CompareTo(db);
            return true;
        }
        return false;
    }

    private static string DedupeKey(DocumentChunk chunk)
    {
        if (chunk.MetadataJson?.Contains("\"RecordType\":\"ExcelRows\"") == true)
        {
            return $"excel:{chunk.DocumentId}:{chunk.ChunkIndex}:{chunk.Namespace}";
        }
        return ContentHash(chunk.Content);
    }

    private static List<string> ExtractDocumentHints(string query)
    {
        var hints = new List<string>();
        var matches = Regex.Matches(query, @"\b[A-Za-z][A-Za-z0-9]{2,}\b");

        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var token = m.Value;
            if (token.Length >= 3 &&
                !token.Equals("the", StringComparison.OrdinalIgnoreCase) &&
                !token.Equals("and", StringComparison.OrdinalIgnoreCase) &&
                !token.Equals("for", StringComparison.OrdinalIgnoreCase) &&
                !token.Equals("with", StringComparison.OrdinalIgnoreCase) &&
                !token.Equals("what", StringComparison.OrdinalIgnoreCase) &&
                !token.Equals("which", StringComparison.OrdinalIgnoreCase) &&
                !token.Equals("this", StringComparison.OrdinalIgnoreCase) &&
                !token.Equals("that", StringComparison.OrdinalIgnoreCase))
            {
                hints.Add(token);
            }
        }
        return hints;
    }
}