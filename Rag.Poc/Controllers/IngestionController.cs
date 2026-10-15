using Microsoft.AspNetCore.Mvc;
using Rag.Poc.Services;

namespace Rag.Poc.Controllers;

[Route("api/[controller]")]
[ApiController]
public class IngestionController : ControllerBase
{
    private readonly DocumentIngestionService _ingestionService;

    public IngestionController(
        DocumentIngestionService ingestionService)
    {
        _ingestionService = ingestionService;
    }

    [HttpPost("projects/{projectId:guid}")]
    public async Task<IActionResult> ProcessProject(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var result =
            await _ingestionService.ProcessProjectAsync(
                projectId,
                cancellationToken);

        return Ok(result);
    }

    [HttpPost("projects/{projectId:guid}/rechunk")]
    public async Task<IActionResult> RechunkProject(
    Guid projectId,
    CancellationToken cancellationToken)
    {
        var processedDocuments =
            await _ingestionService.RechunkProjectAsync(
                projectId,
                cancellationToken);

        return Ok(new
        {
            projectId,
            processedDocuments
        });
    }
}
