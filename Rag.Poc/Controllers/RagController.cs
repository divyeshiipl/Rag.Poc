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

    public RagController(
        RagAnswerService ragAnswerService,
        DocumentSearchService searchService)
    {
        _ragAnswerService = ragAnswerService;
        _searchService = searchService;
    }

    [HttpGet]
    public async Task<IActionResult> Ask(
        [FromQuery] string question,
        [FromQuery] int topK = 5,
        [FromQuery] string? ns = null,
        [FromQuery] double? minSimilarity = null,
        [FromQuery] string? category = null,
        [FromQuery] string? policyType = null,
        [FromQuery] bool hybrid = true,
        [FromQuery] bool rerank = false,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var options = new RetrievalOptions
        {
            Query = question,
            TopK = topK,
            Namespace = ns,
            MinSimilarity = minSimilarity,
            CategoryFilter = string.IsNullOrWhiteSpace(category)
                ? null
                : category.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            PolicyTypeFilter = policyType,
            UseHybrid = hybrid,
            UseReranker = rerank
        };

        var chunks = await _searchService.SearchDetailedAsync(options, cancellationToken);

        if (chunks.Count == 0)
        {
            stopwatch.Stop();
            return Ok(new
            {
                question,
                answer = "I couldn't find relevant information in our company documents.",
                sources = Array.Empty<object>(),
                executionTimeMinutes = Math.Round(stopwatch.Elapsed.TotalMinutes, 2)
            });
        }

        var answer = await _ragAnswerService.AnswerFromChunksAsync(
            question, chunks.Select(x => x.Chunk).ToList(), cancellationToken);

        var sources = chunks.Select(x => new
        {
            documentId = x.Chunk.DocumentId,
            documentName = x.Chunk.Document?.FileName,
            chunkIndex = x.Chunk.ChunkIndex,
            section = x.Chunk.SectionTitle,
            sourceLocation = x.Chunk.SourceLocation,
            score = Math.Round(x.Score, 4),
            retrievedBy = x.Source
        }).ToList();

        stopwatch.Stop();

        return Ok(new
        {
            question,
            answer,
            sources,
            executionTimeMinutes = Math.Round(stopwatch.Elapsed.TotalMinutes, 2)
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