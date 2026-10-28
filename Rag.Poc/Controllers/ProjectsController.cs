namespace Rag.Poc.Controllers;

/// <summary>
/// Handles project management, such as creating projects.
/// </summary>
[Route("api/[controller]")]
[ApiController]
public class ProjectsController : ControllerBase
{
    private readonly RagDbContext _db;

    /// <summary>
    /// Creates the controller with the database context.
    /// </summary>
    public ProjectsController(RagDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Creates a new project and saves it to the database.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create(Project project, CancellationToken cancellationToken)
    {
        project.Id = Guid.NewGuid();
        project.CreatedAtUtc = DateTime.UtcNow;

        _db.Projects.Add(project);

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(project);
    }
}
