using Grpc.Core;
using Polly.CircuitBreaker;
using VectorCatalog.Api.Protos;

namespace VectorCatalog.Api.Infrastructure.Resilience;

/// <summary>
/// Resilience wrapper for FAISS index queries via gRPC.
/// Falls back to an empty result set if circuit is open (graceful degradation).
/// </summary>
public class ResilientIndexService
{
    private readonly IndexService.IndexServiceClient _client;
    private readonly ILogger<ResilientIndexService> _logger;

    private static readonly Polly.IAsyncPolicy<SearchResponse> _searchPolicy =
        ResiliencePolicies.GetCombinedGrpcPolicy<SearchResponse>(
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            "SearchIndex",
            timeoutSeconds: 5);

    public ResilientIndexService(IndexService.IndexServiceClient client, ILogger<ResilientIndexService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _searchPolicy.ExecuteAsync(
                ct => _client.SearchIndexAsync(request, cancellationToken: ct).ResponseAsync,
                cancellationToken);
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError("Index search circuit breaker OPEN — returning empty results (graceful degradation)");
            // Graceful degradation: return empty results rather than 503
            return new SearchResponse
            {
                ShardKey = request.ShardKey,
                SearchLatencyMs = 0,
                CacheHit = false
            };
        }
    }
}
