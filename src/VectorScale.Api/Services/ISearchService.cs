using VectorScale.Api.Models;

namespace VectorScale.Api.Services;

public interface ISearchService
{
    /// <summary>
    /// Full pipeline: generate embedding → check cache → query FAISS → return results.
    /// </summary>
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);
}
