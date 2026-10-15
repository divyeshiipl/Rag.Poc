using Microsoft.AspNetCore.Mvc;
using Rag.Poc.Data;
using Rag.Poc.Models;

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
