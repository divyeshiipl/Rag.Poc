namespace Rag.Poc.Controllers;

public class UiController : Controller
{
    public IActionResult Index() => View();

    public IActionResult Rag() => View(new RagViewModel());

    public IActionResult Ingestion() => View(new IngestionViewModel());

    public IActionResult DocumentSearch() => View(new DocumentSearchViewModel());

    public IActionResult Projects() => View(new ProjectsViewModel());
}