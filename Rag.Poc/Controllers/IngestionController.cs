namespace Rag.Poc.Controllers;

/// <summary>
/// Handles document ingestion operations like chunking and re-chunking for a project.
/// </summary>
[Route("api/[controller]")]
[ApiController]
public class IngestionController : ControllerBase
{
    private readonly DocumentIngestionService _ingestionService;

    /// <summary>
    /// Creates the controller with the ingestion service.
    /// </summary>
    public IngestionController(DocumentIngestionService ingestionService)
    {
        _ingestionService = ingestionService;
    }

    /// <summary>
    /// Processes all documents in a project and creates text chunks.
    /// </summary>
    [HttpPost("projects/{projectId:guid}")]
    public async Task<IActionResult> ProcessProject(Guid projectId, CancellationToken cancellationToken = default)
    {
        var result = await _ingestionService.ProcessProjectAsync(projectId, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Rebuilds the chunks for all documents in a project.
    /// </summary>
    [HttpPost("projects/{projectId:guid}/rechunk")]
    public async Task<IActionResult> RechunkProject(Guid projectId)
    {
        var processed = await _ingestionService.RechunkProjectAsync(projectId);

        return Ok(new { projectId, processedDocuments = processed });
    }
}