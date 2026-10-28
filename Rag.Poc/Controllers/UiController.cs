namespace Rag.Poc.Controllers;

/// <summary>
/// Serves the Razor views for the app's UI pages.
/// </summary>
public class UiController : Controller
{
    /// <summary>
    /// Renders the application's home/index page.
    /// </summary>
    public IActionResult Index() => View();

    /// <summary>
    /// Renders the RAG (question answering) page with an empty view model.
    /// </summary>
    public IActionResult Rag() => View(new RagViewModel());

    /// <summary>
    /// Renders the document ingestion page with an empty view model.
    /// </summary>
    public IActionResult Ingestion() => View(new IngestionViewModel());

    /// <summary>
    /// Renders the document search page with an empty view model.
    /// </summary>
    public IActionResult DocumentSearch() => View(new DocumentSearchViewModel());

    /// <summary>
    /// Renders the projects management page with an empty view model.
    /// </summary>
    public IActionResult Projects() => View(new ProjectsViewModel());
}