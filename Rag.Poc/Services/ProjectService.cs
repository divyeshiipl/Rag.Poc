namespace Rag.Poc.Services;

public class ProjectService
{
    private readonly RagDbContext _db;

    public ProjectService(RagDbContext db)
    {
        _db = db;
    }

    #region Public Methods

    /// <summary>
    /// To Add a new Project
    /// </summary>
    /// <param name="name"></param>
    /// <param name="folder"></param>
    /// <param name="description"></param>
    /// <returns></returns>
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
    /// To get project by projectid
    /// </summary>
    /// <param name="projectId"></param>
    /// <returns></returns>
    public async Task<Project?> GetAsync(Guid projectId)
    {
        return await _db.Projects.Include(x => x.Documents).FirstOrDefaultAsync(x => x.Id == projectId);
    }

    #endregion
}
