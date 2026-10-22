using Microsoft.AspNetCore.Mvc;
using Rag.Poc.Models;
using Rag.Poc.Services;
using System.Diagnostics;

namespace Rag.Poc.Controllers;

[Route("api/[controller]")]
[ApiController]
public class RagController : ControllerBase
{
    private readonly RagAnswerService _ragAnswerService;
    private readonly DocumentSearchService _searchService;
    private readonly IRetrievalPlanner _retrievalPlanner;

    public RagController(
        RagAnswerService ragAnswerService,
        DocumentSearchService searchService,
        IRetrievalPlanner retrievalPlanner)
    {
        _ragAnswerService = ragAnswerService;
        _searchService = searchService;
        _retrievalPlanner = retrievalPlanner;
    }

    [HttpGet]
    public async Task<IActionResult> Ask(
       [FromQuery] string question,
       [FromQuery] int? topK = null,
       [FromQuery] string? ns = null,
       CancellationToken cancellationToken = default)
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

            // Meta / greeting -> answer without retrieval.
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

        // Effective topK: explicit caller wins, else plan, else default.
        var effectiveTopK = topK ?? plan.TopK ?? 6;

        // Effective namespaces: explicit caller wins, else plan, else default.
        var effectiveNamespaces = !string.IsNullOrWhiteSpace(ns)
            ? new List<string> { ns }
            : plan.Namespaces.Count > 0
                ? plan.Namespaces.ToList()
                : new List<string> { "default" };

        var retrieval = Stopwatch.StartNew();
        var chunks = new List<ScoredChunk>();

        try
        {
            foreach (var nsName in effectiveNamespaces)
            {
                var hits = await _searchService.SearchAsync(
                    plan with { Namespaces = new List<string> { nsName } },
                    effectiveTopK,
                    nsName,
                    cancellationToken);

                chunks.AddRange(hits);
            }
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
        retrieval.Stop();

        // Dedupe by (DocumentId, ChunkIndex), keep highest score, apply score floor.
        var minScore = plan.MinScore ?? 0.15;
        var deduped = chunks
            .Where(c => c.Score >= minScore)
            .GroupBy(c => (c.Chunk.DocumentId, c.Chunk.ChunkIndex))
            .Select(g => g.OrderByDescending(c => c.Score).First())
            .OrderByDescending(c => c.Score)
            .Take(effectiveTopK)
            .ToList();

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
            answer = await _ragAnswerService.AnswerFromChunksAsync(
                question,
                deduped.Select(x => x.Chunk).ToList(),
                cancellationToken);
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

    [HttpGet("inspect")]
    public async Task<IActionResult> InspectRetrieval(
        [FromQuery] string question,
        [FromQuery] int topK = 10,
        [FromQuery] string? ns = null,
        [FromQuery] bool hybrid = true,
        CancellationToken cancellationToken = default)
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
}