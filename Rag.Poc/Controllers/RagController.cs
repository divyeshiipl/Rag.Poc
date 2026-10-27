namespace Rag.Poc.Controllers;

[Route("api/[controller]")]
[ApiController]
public class RagController : ControllerBase
{
    private readonly RagAnswerService _ragAnswerService;
    private readonly DocumentSearchService _searchService;
    private readonly IRetrievalPlanner _retrievalPlanner;

    public RagController(RagAnswerService ragAnswerService, DocumentSearchService searchService, IRetrievalPlanner retrievalPlanner)
    {
        _ragAnswerService = ragAnswerService;
        _searchService = searchService;
        _retrievalPlanner = retrievalPlanner;
    }

    /// <summary>
    /// To ask a question
    /// </summary>
    /// <param name="question"></param>
    /// <param name="topK"></param>
    /// <param name="ns"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    [HttpGet]
    public async Task<IActionResult> Ask([FromQuery] string question, [FromQuery] int? topK = null, [FromQuery] string? ns = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return BadRequest(new { error = "Question cannot be empty." });

        if (topK is <= 0 or > 50)
            return BadRequest(new { error = "topK must be between 1 and 50." });

        var total = Stopwatch.StartNew();

        RetrievalPlan plan;
        long planningMs;

        try
        {
            var planning = Stopwatch.StartNew();
            plan = await _retrievalPlanner.CreatePlanAsync(question, cancellationToken);
            planning.Stop();
            planningMs = planning.ElapsedMilliseconds;

            if (plan.Mode == RetrievalMode.Meta)
            {
                total.Stop();
                return Ok(new
                {
                    question,
                    answer = "Hi! I can answer questions about our company documents — policies, tables, reports, and so on. What would you like to know?",
                    sources = Array.Empty<object>(),
                    mode = plan.Mode.ToString(),
                    planningTimeMs = planningMs,
                    retrievalTimeMs = 0,
                    answerGenerationTimeMs = 0,
                    totalTimeMs = total.ElapsedMilliseconds
                });
            }
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }

        var effectiveTopK = topK ?? plan.TopK ?? 6;

        var effectiveNamespaces = !string.IsNullOrWhiteSpace(ns)
            ? new List<string> { ns }
            : plan.Namespaces.Count > 0
                ? plan.Namespaces.ToList()
                : new List<string> { "default" };

        var namespacesToQuery = (plan.Mode == RetrievalMode.Exhaustive && plan.Filters is { Count: > 0 })
                ? new List<string> { Namespaces.Structural }
                : effectiveNamespaces;

        var retrieval = Stopwatch.StartNew();
        var chunks = new List<ScoredChunk>();

        try
        {
            foreach (var nsName in namespacesToQuery)
            {
                var hits = await _searchService.SearchAsync(plan with { Namespaces = new List<string> { nsName } }, effectiveTopK, nsName, cancellationToken);
                chunks.AddRange(hits);
            }
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
        retrieval.Stop();

        var isFiltered = plan.Mode == RetrievalMode.Exhaustive && plan.Filters is { Count: > 0 };
        var isFullTable = plan.Mode == RetrievalMode.FullTable;
        var isAggregate = plan.Mode == RetrievalMode.Aggregate;

        var minScore = isFiltered || isFullTable || isAggregate ? 0.0 : (plan.MinScore ?? 0.15);

        var deduped = chunks
            .Where(c => c.Score >= minScore)
            .GroupBy(c => IsExcelChunk(c.Chunk)
                ? $"excel|{c.Chunk.Id}|{c.Source}"
                : $"prose|{c.Chunk.DocumentId}|{c.Chunk.ChunkIndex}|{c.Chunk.Namespace}")
            .Select(g => g.OrderByDescending(c => c.Score).First())
            .OrderByDescending(c => c.Score)
            .ToList();

        if (!isFiltered && !isFullTable && !isAggregate)
        {
            deduped = deduped.Take(effectiveTopK).ToList();
        }

        if (deduped.Count == 0)
        {
            total.Stop();
            return Ok(new
            {
                question,
                answer = "I couldn't find relevant information in our company documents.",
                sources = Array.Empty<object>(),
                mode = plan.Mode.ToString(),
                retrievalTimeMs = retrieval.ElapsedMilliseconds,
                totalTimeMs = total.ElapsedMilliseconds
            });
        }

        var generation = Stopwatch.StartNew();
        string answer;

        try
        {
            var chunksForLlm = deduped
                .Where(x => x.Source != "filtered-column")
                .Select(x => x.Chunk)
                .ToList();

            var isFilteredQuery = plan.Mode == RetrievalMode.Exhaustive && plan.Filters is { Count: > 0 };

            if (isFilteredQuery && chunksForLlm.Count == 1 && chunksForLlm[0].Content?.Contains("VERIFIED MATCHING ROWS") == true)
            {
                answer = await GenerateBatchedFilteredAnswerAsync(question, chunksForLlm[0], plan.Filters, cancellationToken);
            }
            else
            {
                answer = await _ragAnswerService.AnswerFromChunksAsync(question, chunksForLlm, plan.Filters, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
        generation.Stop();

        var sources = deduped.Select(x => new
        {
            documentId = x.Chunk.DocumentId,
            documentName = x.Chunk.Document?.FileName,
            chunkIndex = x.Chunk.ChunkIndex,
            section = x.Chunk.SectionTitle,
            sourceLocation = x.Chunk.SourceLocation,
            score = Math.Round(x.Score, 4),
            retrievedBy = x.Source
        }).ToList();

        total.Stop();

        return Ok(new
        {
            question,
            answer,
            mode = plan.Mode.ToString(),
            searchQuery = plan.SearchQuery,
            filterText = plan.FilterText,
            sources,
            planningTimeMs = planningMs,
            retrievalTimeMs = retrieval.ElapsedMilliseconds,
            answerGenerationTimeMs = generation.ElapsedMilliseconds,
            totalTimeMs = total.ElapsedMilliseconds
        });
    }

    /// <summary>
    /// To get the retrived chunks/documents of a asked question
    /// </summary>
    /// <param name="question"></param>
    /// <param name="topK"></param>
    /// <param name="ns"></param>
    /// <param name="hybrid"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    [HttpGet("inspect")]
    public async Task<IActionResult> InspectRetrieval([FromQuery] string question, [FromQuery] int topK = 10, [FromQuery] string? ns = null, [FromQuery] bool hybrid = true, CancellationToken cancellationToken = default)
    {
        var chunks = await _searchService.SearchDetailedAsync(new RetrievalOptions
        {
            Query = question,
            TopK = topK,
            Namespace = ns,
            UseHybrid = hybrid
        }, cancellationToken);

        return Ok(new
        {
            question,
            namespaceUsed = ns ?? "(all)",
            hybrid,
            count = chunks.Count,
            chunks = chunks.Select(c => new
            {
                c.Chunk.Id,
                c.Chunk.DocumentId,
                documentName = c.Chunk.Document?.FileName,
                c.Chunk.ChunkIndex,
                c.Chunk.SectionTitle,
                c.Chunk.SourceLocation,
                c.Chunk.TokenCount,
                c.Chunk.Namespace,
                score = Math.Round(c.Score, 4),
                retrievedBy = c.Source,
                c.Chunk.Content
            })
        });
    }

    #region Private Methods

    /// <summary>
    /// To generate the batched filtered answers
    /// </summary>
    /// <param name="question"></param>
    /// <param name="filteredChunk"></param>
    /// <param name="filters"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<string> GenerateBatchedFilteredAnswerAsync(string question, DocumentChunk filteredChunk, IReadOnlyList<QueryFilter>? filters, CancellationToken cancellationToken)
    {
        const int MaxRowsPerBatch = 8;

        var lines = (filteredChunk.Content ?? "").Split('\n');
        var headerLines = new List<string>();
        var rowLines = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("Row ", StringComparison.OrdinalIgnoreCase))
                rowLines.Add(line.TrimEnd());
            else if (!string.IsNullOrWhiteSpace(line))
                headerLines.Add(line.TrimEnd());
        }

        if (rowLines.Count == 0)
            return "No matching rows were found.";

        if (rowLines.Count <= MaxRowsPerBatch)
        {
            return await _ragAnswerService.AnswerFromChunksAsync(question, new[] { filteredChunk }, filters, cancellationToken);
        }

        var batches = new List<List<string>>();
        for (var i = 0; i < rowLines.Count; i += MaxRowsPerBatch)
            batches.Add(rowLines.Skip(i).Take(MaxRowsPerBatch).ToList());

        var allRows = new List<string>();

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchContent = new StringBuilder();
            foreach (var h in headerLines) batchContent.AppendLine(h);
            batchContent.AppendLine();
            foreach (var row in batch) batchContent.AppendLine(row);

            var batchChunk = new DocumentChunk
            {
                Id = filteredChunk.Id,
                DocumentId = filteredChunk.DocumentId,
                ChunkIndex = filteredChunk.ChunkIndex,
                Content = batchContent.ToString(),
                TokenCount = batchContent.Length / 4,
                SectionTitle = filteredChunk.SectionTitle,
                SourceLocation = filteredChunk.SourceLocation,
                Namespace = filteredChunk.Namespace,
                MetadataJson = filteredChunk.MetadataJson,
                Document = filteredChunk.Document
            };

            var batchAnswer = await _ragAnswerService.AnswerFromChunksAsync(
                question, new[] { batchChunk }, filters, cancellationToken);

            var extractedFromBatch = ExtractRowLines(batchAnswer);

            if (extractedFromBatch.Count == 0)
                extractedFromBatch = batch;

            allRows.AddRange(extractedFromBatch);
        }

        var dedupedRows = new Dictionary<int, string>();
        foreach (var row in allRows)
        {
            var num = ExtractRowNumber(row);
            if (num > 0 && !dedupedRows.ContainsKey(num))
                dedupedRows[num] = row;
        }

        var sb = new StringBuilder();
        foreach (var kvp in dedupedRows.OrderBy(k => k.Key))
            sb.AppendLine(kvp.Value);
        sb.AppendLine();
        sb.AppendLine($"Found {dedupedRows.Count} rows.");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// To extract the row lines
    /// </summary>
    /// <param name="answer"></param>
    /// <returns></returns>
    private static List<string> ExtractRowLines(string answer)
    {
        var rows = new List<string>();
        if (string.IsNullOrWhiteSpace(answer)) return rows;

        foreach (var rawLine in answer.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.StartsWith("**")) line = line.TrimStart('*');
            if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("• "))
                line = line.Substring(2).Trim();
            if (line.Length > 2 && char.IsDigit(line[0]) && line[1] == '.' && line[2] == ' ')
                line = line.Substring(3).Trim();
            if (line.EndsWith("**")) line = line.TrimEnd('*');

            if (line.StartsWith("Row ", StringComparison.OrdinalIgnoreCase))
                rows.Add(line.TrimEnd());
        }

        return rows;
    }

    /// <summary>
    /// To extract the row number
    /// </summary>
    /// <param name="rowLine"></param>
    /// <returns></returns>
    private static int ExtractRowNumber(string rowLine)
    {
        if (string.IsNullOrWhiteSpace(rowLine)) return -1;
        var colon = rowLine.IndexOf(':');
        if (colon < 4) return -1;
        var numPart = rowLine.Substring(4, colon - 4).Trim();
        return int.TryParse(numPart, out var n) ? n : -1;
    }

    /// <summary>
    /// To check if chunks are excel chunks or not
    /// </summary>
    /// <param name="chunk"></param>
    /// <returns></returns>
    private static bool IsExcelChunk(DocumentChunk chunk)
    {
        if (chunk.SourceLocation?.StartsWith("Worksheet:", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        if (chunk.MetadataJson?.Contains("\"RecordType\":\"Excel", StringComparison.Ordinal) == true)
            return true;

        return false;
    }
    #endregion
}