using Microsoft.AspNetCore.Mvc;
using Rag.Poc.Services;

namespace Rag.Poc.Controllers;

[Route("api/[controller]")]
[ApiController]
public class IngestionController : ControllerBase
{
    private readonly DocumentIngestionService _ingestionService;

    public IngestionController(DocumentIngestionService ingestionService)
    {
        _ingestionService = ingestionService;
    }

    [HttpPost("projects/{projectId:guid}")]
    public async Task<IActionResult> ProcessProject(
        Guid projectId,
        [FromQuery] string ns = "default",
        CancellationToken cancellationToken = default)
    {
        var result = await _ingestionService.ProcessProjectAsync(projectId, ns, cancellationToken);
        return Ok(result);
    }

    [HttpPost("projects/{projectId:guid}/rechunk")]
    public async Task<IActionResult> RechunkProject(
        Guid projectId,
        [FromQuery] string ns = "default",
        [FromQuery] int chunkSize = 600,
        [FromQuery] int overlap = 100,
        [FromQuery] int minChunkChars = 150,
        CancellationToken cancellationToken = default)
    {
        var processed = await _ingestionService.RechunkProjectAsync(
            projectId, ns, chunkSize, overlap, minChunkChars, cancellationToken);

        return Ok(new { projectId, ns, chunkSize, overlap, minChunkChars, processedDocuments = processed });
    }
}