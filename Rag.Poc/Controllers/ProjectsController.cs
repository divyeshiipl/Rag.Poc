namespace Rag.Poc.Controllers;

[Route("api/[controller]")]
[ApiController]
public class ProjectsController : ControllerBase
{
    private readonly RagDbContext _db;

    public ProjectsController(RagDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// To Add a new project
    /// </summary>
    /// <param name="project"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
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
