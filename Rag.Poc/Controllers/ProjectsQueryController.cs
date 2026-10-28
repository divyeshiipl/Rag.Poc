namespace Rag.Poc.Controllers;

/// <summary>
/// Handles read-only project queries like listing projects and their documents.
/// </summary>
[Route("api/[controller]")]
[ApiController]
public class ProjectsQueryController : ControllerBase
{
    private readonly RagDbContext _db;

    /// <summary>
    /// Creates the controller with the database context.
    /// </summary>
    public ProjectsQueryController(RagDbContext db) => _db = db;

    /// <summary>
    /// Gets all projects, newest first, with their document counts.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var projects = await _db.Projects
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Description,
                p.DocumentFolder,
                p.CreatedAtUtc,
                DocumentCount = p.Documents.Count
            })
            .OrderByDescending(p => p.CreatedAtUtc)
            .ToListAsync(ct);

        return Ok(projects);
    }

    /// <summary>
    /// Gets all documents for a project, ordered by file name.
    /// </summary>
    [HttpGet("{id:guid}/documents")]
    public async Task<IActionResult> GetDocuments(Guid id, CancellationToken ct)
    {
        var docs = await _db.Documents
            .Where(d => d.ProjectId == id)
            .Select(d => new
            {
                d.Id,
                d.FileName,
                d.ContentType,
                d.FileSize,
                d.Status,
                d.CreatedAtUtc,
                d.ProcessedAtUtc,
                ChunkCount = d.Chunks.Count
            })
            .OrderBy(d => d.FileName)
            .ToListAsync(ct);

        return Ok(docs);
    }
}