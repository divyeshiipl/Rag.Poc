namespace Rag.Poc.ViewModels;

public class ProjectsViewModel
{
    public List<ProjectListItemViewModel> Projects { get; set; } = [];
}

public class ProjectListItemViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DocumentFolder { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public int DocumentCount { get; set; }
}