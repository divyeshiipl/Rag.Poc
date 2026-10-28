namespace Rag.Poc.Services;

/// <summary>
/// Provides functionality for searching documents based on various criteria.
/// </summary>
public sealed class DocumentSearchService
{
    private static readonly Regex IdentifierRegex = new(@"\b[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TokenRegex = new(@"[\p{L}\p{N}]+(?:[._/-][\p{L}\p{N}]+)*", RegexOptions.Compiled);
    private static readonly HashSet<string> StopWords = new(
        new[]
        {
            "a", "an", "the", "is", "are", "was", "were",
            "what", "which", "who", "when", "where", "why",
            "how", "do", "does", "did", "has", "have", "had",
            "in", "on", "at", "of", "for", "to", "from",
            "with", "by", "and", "or", "be", "been", "being",
            "about", "please", "tell", "me", "can", "could",
            "would", "should", "it", "this", "that"
        },
        StringComparer.OrdinalIgnoreCase);

    private readonly RagDbContext _db;
    private readonly IEmbeddingService _embeddingService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentSearchService"/> class.
    /// </summary>
    /// <param name="db"></param>
    /// <param name="embeddingService"></param>
    public DocumentSearchService(RagDbContext db, IEmbeddingService embeddingService)
    {
        _db = db;
        _embeddingService = embeddingService;
    }

    /// <summary>
    /// Searches for documents based on the provided query.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="topK">The maximum number of results to return.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A list of document chunks that match the query.</returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    private async Task<List<DocumentChunk>> SearchSingleAsync(string query, int topK, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query cannot be empty.", nameof(query));

        if (topK <= 0)
            throw new ArgumentOutOfRangeException(nameof(topK));

        var identifierMatch = IdentifierRegex.Match(query);

        if (identifierMatch.Success)
        {
            var identifier = identifierMatch.Value.ToLowerInvariant();

            var exactMatches = await _db.DocumentChunks
                .AsNoTracking()
                .Include(x => x.Document)
                .Where(x =>
                    (x.Document != null && (x.Document.FileName.ToLower().Contains(identifier) || (x.Document.RelativePath != null
                    && x.Document.RelativePath.ToLower().Contains(identifier)))) || x.Content.ToLower().Contains(identifier) || (x.SectionTitle != null
                    && x.SectionTitle.ToLower().Contains(identifier)) || (x.MetadataJson != null && x.MetadataJson.ToLower().Contains(identifier)))
                .OrderByDescending<DocumentChunk, bool>(x => x.Document != null && x.Document.FileName.ToLower().Contains(identifier))
                .ThenBy(x => x.ChunkIndex)
                .Take(topK)
                .ToListAsync(cancellationToken);

            if (exactMatches.Count > 0)
                return exactMatches;
        }

        var searchTerms = TokenRegex.Matches(query)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(t => t.Length > 2 && !StopWords.Contains(t))
            .Distinct()
            .ToArray();

        var embedding = await _embeddingService.GenerateQueryEmbeddingAsync(query, cancellationToken);

        var queryVector = new Vector(embedding);
        var candidateCount = Math.Max(topK * 20, 100);

        var semanticResults = await _db.DocumentChunks
            .AsNoTracking()
            .Include(x => x.Document)
            .Where(x => x.Embedding != null)
            .OrderBy(x => x.Embedding!.CosineDistance(queryVector))
            .Take(candidateCount)
            .ToListAsync(cancellationToken);

        var keywordResults = new List<DocumentChunk>();

        if (searchTerms.Length > 0)
        {
            var predicate = BuildKeywordPredicate(searchTerms);
            var matchCount = BuildKeywordMatchCount(searchTerms);

            keywordResults = await _db.DocumentChunks
                .AsNoTracking()
                .Include(x => x.Document)
                .Where(predicate)
                .OrderByDescending(matchCount)
                .ThenBy(x => x.ChunkIndex)
                .Take(candidateCount * 10)
                .ToListAsync(cancellationToken);

            keywordResults = keywordResults
                .OrderByDescending(x =>
                    ScoreKeywordMatch(x, query, searchTerms))
                .ThenBy(x => x.ChunkIndex)
                .Take(candidateCount)
                .ToList();
        }

        var versionMatch = Regex.Match(query, @"\b(?:v(?:ersion)?\s*)?(\d+(?:\.\d+)+)\b", RegexOptions.IgnoreCase);
        var versionResults = new List<DocumentChunk>();

        if (versionMatch.Success)
        {
            var version = versionMatch.Groups[1].Value;

            versionResults = await _db.DocumentChunks
                .AsNoTracking()
                .Include(x => x.Document)
                .Where(x =>
                    x.Document != null &&
                    x.Document.FileName.ToLower().Contains(version.ToLower()))
                .ToListAsync(cancellationToken);

            versionResults = versionResults
                .OrderByDescending(x =>
                    ScoreKeywordMatch(x, query, searchTerms))
                .ThenBy(x => x.ChunkIndex)
                .Take(candidateCount)
                .ToList();
        }

        const double rrfConstant = 60;

        var scores = new Dictionary<string, double>();
        var chunks = new Dictionary<string, DocumentChunk>();

        AddRankedResults(semanticResults, scores, chunks, rrfConstant, false, query, searchTerms);
        AddRankedResults(keywordResults, scores, chunks, rrfConstant, true, query, searchTerms);
        AddRankedResults(versionResults, scores, chunks, rrfConstant, true, query, searchTerms);

        foreach (var chunk in versionResults)
        {
            var key = $"{chunk.DocumentId}:{chunk.ChunkIndex}";
            scores[key] += 0.05;
        }

        foreach (var pair in chunks)
            scores[pair.Key] += ScoreEvidenceMatch(query, pair.Value) * 0.0001;

        var rankedChunks = chunks
            .OrderByDescending(x => scores[x.Key])
            .Take(topK)
            .Select(x => x.Value)
            .ToList();

        var keywordEvidence = keywordResults
            .OrderByDescending(x => ScoreEvidenceMatch(query, x))
            .ThenByDescending(x => ScoreKeywordMatch(x, query, searchTerms))
            .Take(topK)
            .ToList();

        var finalCandidates = rankedChunks
            .Concat(keywordEvidence)
            .GroupBy(x => $"{x.DocumentId}:{x.ChunkIndex}")
            .Select(x => x.First())
            .ToList();

        return await ExpandWithNeighborsAsync(finalCandidates, query, cancellationToken);
    }

    /// <summary>
    /// Expands the ranked chunks with their neighboring chunks.
    /// </summary>
    /// <param name="rankedChunks"></param>
    /// <param name="query"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<List<DocumentChunk>> ExpandWithNeighborsAsync(IReadOnlyList<DocumentChunk> rankedChunks, string query, CancellationToken cancellationToken)
    {
        if (rankedChunks.Count == 0)
            return new List<DocumentChunk>();

        var result = rankedChunks
            .GroupBy(x => $"{x.DocumentId}:{x.ChunkIndex}")
            .Select(x => x.First())
            .ToList();

        var addedKeys = result.Select(x => $"{x.DocumentId}:{x.ChunkIndex}").ToHashSet();

        foreach (var group in rankedChunks.GroupBy(x => x.DocumentId))
        {
            var indexes = group
                .SelectMany(x => new[] { x.ChunkIndex - 1, x.ChunkIndex + 1 })
                .Where(x => x >= 0)
                .Distinct()
                .ToArray();

            if (indexes.Length == 0)
                continue;

            var neighbors = await _db.DocumentChunks
                .AsNoTracking()
                .Include(x => x.Document)
                .Where(x =>
                    x.DocumentId == group.Key &&
                    indexes.Contains(x.ChunkIndex))
                .ToListAsync(cancellationToken);

            foreach (var neighbor in neighbors)
            {
                var key = $"{neighbor.DocumentId}:{neighbor.ChunkIndex}";

                if (addedKeys.Contains(key))
                    continue;

                var relevant = ScoreEvidenceMatch(query, neighbor) > 0;

                var sharesSection = group.Any(x =>
                    !string.IsNullOrWhiteSpace(x.SectionTitle) &&
                    string.Equals(
                        x.SectionTitle,
                        neighbor.SectionTitle,
                        StringComparison.OrdinalIgnoreCase));

                if (!relevant && !sharesSection)
                    continue;

                addedKeys.Add(key);
                result.Add(neighbor);
            }
        }

        return result;
    }

    /// <summary>
    /// Searches for documents based on the provided query.
    /// </summary>
    /// <param name="query"></param>
    /// <param name="topK"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentOutOfRangeException"></exception>

    public async Task<List<DocumentChunk>> SearchAsync(string query, int topK = 5, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query cannot be empty.", nameof(query));

        if (topK <= 0)
            throw new ArgumentOutOfRangeException(nameof(topK));

        var queries = SplitIntoSearchQueries(query);
        var results = new List<DocumentChunk>();
        var seen = new HashSet<string>();
        var bestQueryScores = new Dictionary<string, int>();

        foreach (var searchQuery in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunks = await SearchSingleAsync(searchQuery, topK, cancellationToken);

            foreach (var chunk in chunks)
            {
                var key = $"{chunk.DocumentId}:{chunk.ChunkIndex}";
                var score = ScoreEvidenceMatch(searchQuery, chunk);

                if (!bestQueryScores.TryGetValue(key, out var currentScore) || score > currentScore)
                    bestQueryScores[key] = score;

                if (seen.Add(key))
                    results.Add(chunk);
            }
        }

        var maxResults = Math.Min(20, Math.Max(topK, topK * queries.Count));

        return results
            .OrderByDescending(x =>
                bestQueryScores.GetValueOrDefault(
                    $"{x.DocumentId}:{x.ChunkIndex}"))
            .ThenByDescending(x => ScoreEvidenceMatch(query, x))
            .Take(maxResults)
            .ToList();
    }


    /// <summary>
    /// Splits a search query into multiple sub-queries based on delimiters and logical operators.
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    private static List<string> SplitIntoSearchQueries(string query)
    {
        var queries = new List<string> { query.Trim() };

        var clauses = Regex.Split(query, @"[,;?]+|\band\s+(?=(?:what|which|who|where|when|why|how)\b)|\b(?:also|additionally)\b", RegexOptions.IgnoreCase);

        foreach (var clause in clauses)
        {
            var candidate = clause.Trim();

            var meaningfulTerms = TokenRegex.Matches(candidate)
                .Select(x => x.Value)
                .Where(x => x.Length > 2 && !StopWords.Contains(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            if (meaningfulTerms < 2)
                continue;

            if (!queries.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                queries.Add(candidate);
        }

        return queries;
    }

    #region Private Helper Methods

    /// <summary>
    /// Builds a predicate for filtering document chunks based on keyword matches.
    /// </summary>
    /// <param name="terms"></param>
    /// <returns></returns>
    private static Expression<Func<DocumentChunk, bool>> BuildKeywordPredicate(string[] terms)
    {
        var parameter = Expression.Parameter(typeof(DocumentChunk), "x");
        var matchCount = BuildKeywordMatchCountExpression(parameter, terms);
        var minimumMatches = 1;

        return Expression.Lambda<Func<DocumentChunk, bool>>(Expression.GreaterThanOrEqual(matchCount, Expression.Constant(minimumMatches)), parameter);
    }

    /// <summary>
    /// Builds an expression for calculating the number of keyword matches in a document chunk.
    /// </summary>
    /// <param name="terms"></param>
    /// <returns></returns>
    private static Expression<Func<DocumentChunk, int>> BuildKeywordMatchCount(string[] terms)
    {
        var parameter = Expression.Parameter(typeof(DocumentChunk), "x");

        return Expression.Lambda<Func<DocumentChunk, int>>(BuildKeywordMatchCountExpression(parameter, terms), parameter);
    }

    /// <summary>
    /// Builds an expression for calculating the number of keyword matches in a document chunk.
    /// </summary>
    /// <param name="parameter"></param>
    /// <param name="terms"></param>
    /// <returns></returns>
    private static Expression BuildKeywordMatchCountExpression(ParameterExpression parameter, string[] terms)
    {
        var content = Expression.Property(parameter, nameof(DocumentChunk.Content));
        var section = Expression.Property(parameter, nameof(DocumentChunk.SectionTitle));
        var metadata = Expression.Property(parameter, nameof(DocumentChunk.MetadataJson));
        var document = Expression.Property(parameter, nameof(DocumentChunk.Document));
        var fileName = Expression.Property(document, nameof(Document.FileName));
        var relativePath = Expression.Property(document, nameof(Document.RelativePath));

        Expression total = Expression.Constant(0);

        foreach (var term in terms)
        {
            var value = Expression.Constant(term);
            var documentExists = Expression.NotEqual(document, Expression.Constant(null, document.Type));

            var matches = new Expression[]
            {
            ContainsIgnoreCase(content, value),
            ContainsIgnoreCase(section, value),
            ContainsIgnoreCase(metadata, value),

            Expression.AndAlso(documentExists, ContainsIgnoreCase(fileName, value)),

            Expression.AndAlso(
                Expression.AndAlso(
                    documentExists,
                    Expression.NotEqual(
                        relativePath,
                        Expression.Constant(null, typeof(string)))),
                ContainsIgnoreCase(relativePath, value))
            };

            Expression termMatches = matches[0];

            for (var i = 1; i < matches.Length; i++)
                termMatches = Expression.OrElse(termMatches, matches[i]);

            total = Expression.Add(total, Expression.Condition(termMatches, Expression.Constant(1), Expression.Constant(0)));
        }

        return total;
    }

    /// <summary>
    /// Checks if a field contains a term, ignoring case.
    /// </summary>
    /// <param name="field"></param>
    /// <param name="term"></param>
    /// <returns></returns>
    private static Expression ContainsIgnoreCase(Expression field, Expression term)
    {
        var notNull = Expression.NotEqual(field, Expression.Constant(null, typeof(string)));
        var lowerField = Expression.Call(field, typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!);

        var contains = Expression.Call(
            lowerField,
            typeof(string).GetMethod(
                nameof(string.Contains), new[] { typeof(string) })!,
            term);

        return Expression.AndAlso(notNull, contains);
    }

    /// <summary>
    /// Scores the match between a query and a document chunk.
    /// </summary>
    /// <param name="query"></param>
    /// <param name="chunk"></param>
    /// <returns></returns>
    private static int ScoreEvidenceMatch(string query, DocumentChunk chunk)
    {
        var queryTerms = TokenRegex.Matches(query)
            .Select(x => x.Value.ToLowerInvariant())
            .Where(x => x.Length > 2 && !StopWords.Contains(x))
            .Distinct()
            .ToArray();

        if (queryTerms.Length == 0)
            return 0;

        var content = chunk.Content ?? string.Empty;
        var section = chunk.SectionTitle ?? string.Empty;
        var fileName = chunk.Document?.FileName ?? string.Empty;

        var score = 0;

        var queryVersion = Regex.Match(query, @"\b(?:v(?:ersion)?\s*)?(\d+(?:\.\d+)+)\b", RegexOptions.IgnoreCase);

        if (queryVersion.Success)
        {
            var fileVersion = Regex.Match(fileName, @"\b(?:v(?:ersion)?\s*)?(\d+(?:\.\d+)+)\b", RegexOptions.IgnoreCase);
            var hasFilenameRelevance = queryTerms.Any(term => fileName.Contains(term, StringComparison.OrdinalIgnoreCase));

            if (hasFilenameRelevance && fileVersion.Success &&
                string.Equals(queryVersion.Groups[1].Value, fileVersion.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                score += 40;
        }

        var sectionTerms = TokenRegex.Matches(section).Select(x => x.Value.ToLowerInvariant()).ToHashSet();
        var fileTerms = TokenRegex.Matches(fileName).Select(x => x.Value.ToLowerInvariant()).ToHashSet();

        score += queryTerms.Count(sectionTerms.Contains) * 5;
        score += queryTerms.Count(fileTerms.Contains) * 4;

        var contentTerms = TokenRegex.Matches(content).Select(x => x.Value.ToLowerInvariant()).ToHashSet();

        score += queryTerms.Count(contentTerms.Contains) * 2;

        var matchedQueryTerms = queryTerms.Count(contentTerms.Contains);
        var coverage = (double)matchedQueryTerms / queryTerms.Length;

        if (coverage >= 0.6)
            score += 10;

        if (!string.IsNullOrWhiteSpace(content) && content.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            score += 20;

        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var lineTerms = TokenRegex.Matches(line).Select(x => x.Value.ToLowerInvariant()).ToHashSet();
            var lineMatches = queryTerms.Count(lineTerms.Contains);

            if (lineMatches >= 2)
                score += lineMatches * 3;
        }

        return score;
    }

    /// <summary>
    /// Scores the match between the query and the attribute evidence in the document chunk.
    /// </summary>
    /// <param name="query"></param>
    /// <param name="chunk"></param>
    /// <returns></returns>
    private static int ScoreAttributeEvidenceMatch(string query, DocumentChunk chunk)
    {
        var content = chunk.Content ?? string.Empty;
        var section = chunk.SectionTitle ?? string.Empty;

        var isRoleQuestion = Regex.IsMatch(query, @"\b(role|assigned|designation|team)\b", RegexOptions.IgnoreCase);
        var isStatusQuestion = Regex.IsMatch(query, @"\b(status|state|approval|approved|pending)\b", RegexOptions.IgnoreCase);

        if (!isRoleQuestion && !isStatusQuestion)
            return 0;

        var ignoredTerms = new HashSet<string>(
            StopWords.Concat(new[]
            {
                "role", "assigned", "designation", "team",
                "status", "state", "approval", "approved", "pending",
                "table"
            }),
            StringComparer.OrdinalIgnoreCase);

        var entityTerms = TokenRegex.Matches(query)
            .Select(x => x.Value.ToLowerInvariant())
            .Where(x => x.Length > 2 && !ignoredTerms.Contains(x))
            .Distinct().ToArray();

        if (entityTerms.Length == 0)
            return 0;

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var expectedHeaders = isRoleQuestion
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "role", "designation", "team"
            }
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "status", "approval"
            };

        var score = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var cells = lines[i].Split('|').Select(x => x.Trim()).ToArray();

            if (cells.Length < 2)
                continue;

            var attributeIndex = Array.FindIndex(cells, expectedHeaders.Contains);

            if (attributeIndex < 0)
                continue;

            for (var rowIndex = i + 1; rowIndex < lines.Length; rowIndex++)
            {
                var row = lines[rowIndex].Split('|').Select(x => x.Trim()).ToArray();

                if (row.Length != cells.Length)
                    break;

                if (row.Any(expectedHeaders.Contains))
                    break;

                var entityMatches = row
                    .Where((_, index) => index != attributeIndex)
                    .Any(cell =>
                    {
                        var terms = TokenRegex.Matches(cell)
                            .Select(x => x.Value.ToLowerInvariant())
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        return entityTerms.All(terms.Contains);
                    });

                if (entityMatches && !string.IsNullOrWhiteSpace(row[attributeIndex]))
                    return 1000;
            }
        }

        var contentTerms = TokenRegex.Matches(content).Select(x => x.Value.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entityMatchCount = entityTerms.Count(contentTerms.Contains);

        if (entityMatchCount == 0)
            return 0;

        score += entityMatchCount * 2;

        if (isRoleQuestion && Regex.IsMatch(content + " " + section, @"\b(role|attendee|team|designation|responsibility)\b", RegexOptions.IgnoreCase))
            score += 5;

        if (isStatusQuestion && Regex.IsMatch(content + " " + section, @"\b(approval|status|state)\b", RegexOptions.IgnoreCase))
            score += 5;

        return score;
    }

    /// <summary>
    /// Gets the exact status table matches for the given query and candidates.
    /// </summary>
    /// <param name="query"></param>
    /// <param name="candidates"></param>
    /// <returns></returns>
    private static List<DocumentChunk> GetExactStatusTableMatches(string query, IEnumerable<DocumentChunk> candidates)
    {
        if (!Regex.IsMatch(query, @"\b(roles?|status|approval)\b", RegexOptions.IgnoreCase))
            return new List<DocumentChunk>();

        var statusMatch = Regex.Match(query, @"\b(not\s+approved|in\s+progress|approved|pending|rejected|completed)\b", RegexOptions.IgnoreCase);

        if (!statusMatch.Success)
            return new List<DocumentChunk>();

        var requestedStatus = Regex.Replace(statusMatch.Value, @"\s+", " ").Trim();
        var results = new List<DocumentChunk>();

        foreach (var chunk in candidates)
        {
            var lines = (chunk.Content ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).ToArray();

            for (var i = 0; i < lines.Length; i++)
            {
                var headers = lines[i].Split('|').Select(cell => cell.Trim()).ToArray();

                var roleIndex = Array.FindIndex(headers, header => Regex.IsMatch(header, @"^(role|designation|team)$", RegexOptions.IgnoreCase));

                var statusIndex = Array.FindIndex(headers, header => Regex.IsMatch(header, @"^(status|approval status)$", RegexOptions.IgnoreCase));

                if (roleIndex < 0 || statusIndex < 0)
                    continue;

                var matchingRows = new List<string>();

                for (var j = i + 1; j < lines.Length; j++)
                {
                    var cells = lines[j].Split('|').Select(cell => cell.Trim()).ToArray();

                    if (cells.Length != headers.Length)
                        break;

                    if (cells.Any(cell => Regex.IsMatch(cell, @"^(role|designation|team|status|approval status)$", RegexOptions.IgnoreCase)))
                        break;

                    if (string.IsNullOrWhiteSpace(cells[roleIndex]) || string.IsNullOrWhiteSpace(cells[statusIndex]))
                        continue;

                    var rowStatus = Regex.Replace(cells[statusIndex], @"[*_`]", "").Trim();

                    if (string.Equals(rowStatus, requestedStatus, StringComparison.OrdinalIgnoreCase))
                        matchingRows.Add(lines[j]);
                }

                if (matchingRows.Count == 0)
                    continue;

                chunk.Content = string.Join(Environment.NewLine, new[] { lines[i] }.Concat(matchingRows));

                results.Add(chunk);
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Scores a table row match based on the query and content.
    /// </summary>
    /// <param name="query"></param>
    /// <param name="content"></param>
    /// <returns></returns>
    private static int ScoreTableRowMatch(string query, string content)
    {
        var queryTerms = TokenRegex.Matches(query)
            .Select(x => x.Value.ToLowerInvariant())
            .Where(x => x.Length > 2 && !StopWords.Contains(x))
            .Distinct()
            .ToArray();

        if (queryTerms.Length == 0)
            return 0;

        var score = 0;

        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', 2);

            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                continue;

            var labelTerms = TokenRegex.Matches(parts[0]).Select(x => x.Value.ToLowerInvariant()).ToHashSet();
            var matchingTerms = queryTerms.Count(labelTerms.Contains);

            if (matchingTerms == 0)
                continue;

            score += matchingTerms * 10;

            if (queryTerms.All(labelTerms.Contains))
                score += 15;
        }

        return score;
    }

    /// <summary>
    /// Scores a phrase match based on the query and content.
    /// </summary>
    /// <param name="query"></param>
    /// <param name="content"></param>
    /// <returns></returns>
    private static int ScorePhraseMatch(string query, string content)
    {
        var queryTerms = TokenRegex.Matches(query)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(t => t.Length > 2 && !StopWords.Contains(t))
            .ToArray();

        var contentTerms = TokenRegex.Matches(content)
            .Select(m => m.Value.ToLowerInvariant())
            .ToArray();

        if (queryTerms.Length < 2 || contentTerms.Length < 2)
            return 0;

        var score = 0;

        for (var i = 0; i < queryTerms.Length - 1; i++)
        {
            for (var j = 0; j < contentTerms.Length - 1; j++)
            {
                if (queryTerms[i] == contentTerms[j] && queryTerms[i + 1] == contentTerms[j + 1])
                    score += 20;
            }
        }

        return score;
    }

    /// <summary>
    /// Scores a document chunk based on keyword matches.
    /// </summary>
    /// <param name="chunk"></param>
    /// <param name="query"></param>
    /// <param name="terms"></param>
    /// <returns></returns>
    private static int ScoreKeywordMatch(DocumentChunk chunk, string query, string[] terms)
    {
        var content = chunk.Content ?? string.Empty;
        var section = chunk.SectionTitle ?? string.Empty;
        var metadata = chunk.MetadataJson ?? string.Empty;
        var fileName = chunk.Document?.FileName ?? string.Empty;
        var relativePath = chunk.Document?.RelativePath ?? string.Empty;

        var score = 0;

        if (content.Contains(query, StringComparison.OrdinalIgnoreCase))
            score += 20;

        foreach (var term in terms)
        {
            if (content.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 2;

            if (section.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 3;

            if (metadata.Contains(term, StringComparison.OrdinalIgnoreCase))
                score++;

            if (fileName.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 5;

            if (relativePath.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 2;
        }


        var isPerformanceQuestion = Regex.IsMatch(query, @"\b(performance|target|targets|response\s*times?|latency|benchmark|sla)\b", RegexOptions.IgnoreCase);

        if (isPerformanceQuestion)
        {
            var hasTargetLabel = Regex.IsMatch(content, @"\b(login|asset\s+search|dashboard|report\s+generation)\b", RegexOptions.IgnoreCase);

            var hasExplicitTime = Regex.IsMatch(content, @"(?:<=|<|less\s+than|under)\s*\d+(?:\.\d+)?\s*(?:seconds?|secs?|milliseconds?|ms)\b", RegexOptions.IgnoreCase);

            if (hasTargetLabel && hasExplicitTime)
                score += 50;

            if (Regex.IsMatch(section, @"\b(performance|non-functional requirements|response time)\b", RegexOptions.IgnoreCase))
                score += 20;

            if (Regex.IsMatch(content, @"\b(bug ratio|defect ratio|coding standards|naming conventions|unit testing coverage|test cases)\b", RegexOptions.IgnoreCase))
                score -= 15;
        }

        var queryTerms = terms.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = line.IndexOf('|');

            if (separatorIndex <= 0 || separatorIndex == line.Length - 1)
                continue;

            var label = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            var labelTerms = TokenRegex.Matches(label)
                .Select(m => m.Value.ToLowerInvariant())
                .Where(queryTerms.Contains)
                .Distinct()
                .Count();

            if (labelTerms > 0 && !string.IsNullOrWhiteSpace(value))
                score += 10 * labelTerms;
        }

        score += ScorePhraseMatch(query, content);
        score += ScoreTableRowMatch(query, content);

        return score;
    }

    /// <summary>
    /// Adds ranked results based on their scores.
    /// </summary>
    /// <param name="results"></param>
    /// <param name="scores"></param>
    /// <param name="chunks"></param>
    /// <param name="rrfConstant"></param>
    /// <param name="isKeywordResult"></param>
    /// <param name="query"></param>
    /// <param name="terms"></param>
    private static void AddRankedResults(IReadOnlyList<DocumentChunk> results, Dictionary<string, double> scores, Dictionary<string, DocumentChunk> chunks, double rrfConstant,
        bool isKeywordResult, string query, string[] terms)
    {
        for (var i = 0; i < results.Count; i++)
        {
            var chunk = results[i];
            var key = $"{chunk.DocumentId}:{chunk.ChunkIndex}";

            var score = 1.0 / (rrfConstant + i + 1);
            var keywordScore = ScoreKeywordMatch(chunk, query, terms);

            score += keywordScore * (isKeywordResult ? 0.001 : 0.0005);

            scores[key] = scores.GetValueOrDefault(key) + score;
            chunks[key] = chunk;
        }
    }

    #endregion
}
