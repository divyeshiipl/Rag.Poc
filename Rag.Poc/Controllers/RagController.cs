using Microsoft.AspNetCore.Mvc;
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
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var chunks =
            await _searchService.SearchAsync(
                question,
                5,
                cancellationToken);

        if (chunks.Count == 0)
        {
            stopwatch.Stop();
            return Ok(new
            {
                question,
                answer = "I could not find relevant information in the provided documents.",
                sources = Array.Empty<object>(),
                executionTimeMinutes = Math.Round(stopwatch.Elapsed.TotalMinutes, 2)
            });
        }

        var answer =
            await _ragAnswerService.AnswerAsync(
                question,
                5,
                cancellationToken);

        var sources = chunks
            .Select(x => new
            {
                documentId = x.DocumentId,
                documentName = x.Document?.FileName,
                chunkIndex = x.ChunkIndex,
                section = x.SectionTitle,
                sourceLocation = x.SourceLocation,
            })
            .ToList();

        stopwatch.Stop();

        return Ok(new
        {
            question,
            answer,
            sources,
            executionTimeMinutes = Math.Round(stopwatch.Elapsed.TotalMinutes, 2)
        });
    }
}