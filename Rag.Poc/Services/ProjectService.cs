namespace Rag.Poc.Services;

/// <summary>
/// Handles project management: creating projects and loading them with their documents.
/// </summary>
public class ProjectService
{
    private readonly RagDbContext _db;

    /// <summary>
    /// Creates the service with the database context.
    /// </summary>
    public ProjectService(RagDbContext db)
    {
        _db = db;
    }

    #region Public Methods

    /// <summary>
    /// Creates a new project and saves it to the database.
    /// </summary>
    public async Task<Project> CreateAsync(string name, string folder, string? description = null)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            DocumentFolder = folder,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.Projects.Add(project);
        await _db.SaveChangesAsync();
        return project;
    }

    /// <summary>
    /// Gets a project by id, including its documents (null if not found).
    /// </summary>
    public async Task<Project?> GetAsync(Guid projectId)
    {
        return await _db.Projects.Include(x => x.Documents).FirstOrDefaultAsync(x => x.Id == projectId);
    }

    #endregion
}
