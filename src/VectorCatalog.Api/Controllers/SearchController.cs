using Microsoft.AspNetCore.Mvc;
using VectorCatalog.Api.Models;
using VectorCatalog.Api.Services;

namespace VectorCatalog.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
public class SearchController : ControllerBase
{
    private readonly ISearchService _searchService;
    private readonly ILogger<SearchController> _logger;

    public SearchController(ISearchService searchService, ILogger<SearchController> logger)
    {
        _searchService = searchService;
        _logger = logger;
    }

    /// <summary>
    /// Performs an approximate nearest neighbor search using the provided text query.
    /// </summary>
    /// <param name="request">Search parameters including the query text and result count.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Top-K nearest neighbor results with metadata.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(SearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SearchResponse>> Search(
        [FromBody] SearchRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var response = await _searchService.SearchAsync(request, cancellationToken);
        return Ok(response);
    }
}
