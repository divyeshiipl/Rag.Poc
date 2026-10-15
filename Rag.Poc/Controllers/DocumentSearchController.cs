using Microsoft.AspNetCore.Mvc;
using Rag.Poc.Services;

namespace Rag.Poc.Controllers;

[Route("api/[controller]")]
[ApiController]
public class DocumentSearchController : ControllerBase
{
    private readonly DocumentSearchService _searchService;

    public DocumentSearchController(
        DocumentSearchService searchService)
    {
        _searchService = searchService;
    }

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] string query,
        CancellationToken cancellationToken)
    {
        var results =
            await _searchService.SearchAsync(
                query,
                5,
                cancellationToken);

        return Ok(
            results.Select(x => new
            {
                x.Id,
                x.DocumentId,
                x.ChunkIndex,
                x.SectionTitle,
                x.Content
            }));
    }
}
