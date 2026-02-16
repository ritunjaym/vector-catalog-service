using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VectorCatalog.Api.Configuration;
using VectorCatalog.Api.Infrastructure.Observability;
using VectorCatalog.Api.Infrastructure.Resilience;
using VectorCatalog.Api.Models;
using VectorCatalog.Api.Protos;
using SearchRequest = VectorCatalog.Api.Models.SearchRequest;
using SearchResponse = VectorCatalog.Api.Models.SearchResponse;

namespace VectorCatalog.Api.Services;

public class SearchService : ISearchService
{
    private readonly IEmbeddingService _embeddingService;
    private readonly ICacheService _cacheService;
    private readonly ResilientIndexService _indexService;
    private readonly FaissOptions _faissOptions;
    private readonly ILogger<SearchService> _logger;

    public SearchService(
        IEmbeddingService embeddingService,
        ICacheService cacheService,
        ResilientIndexService indexService,
        IOptions<VectorCatalogOptions> options,
        ILogger<SearchService> logger)
    {
        _embeddingService = embeddingService;
        _cacheService = cacheService;
        _indexService = indexService;
        _faissOptions = options.Value.Faiss;
        _logger = logger;
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();
        var shardKey = request.ShardKey ?? _faissOptions.DefaultShardKey;
        var queryHash = _cacheService.ComputeQueryHash(request.Query, request.TopK, shardKey);

        // 1. Cache-aside: check Redis first
        var cached = await _cacheService.GetAsync<SearchResponse>(queryHash, cancellationToken);
        if (cached is not null)
        {
            cached.CacheHit = true;
            cached.TotalLatencyMs = totalSw.Elapsed.TotalMilliseconds;
            _logger.LogInformation("Cache HIT: hash={Hash}, totalMs={Ms:F1}", queryHash, cached.TotalLatencyMs);
            return cached;
        }

        // Enrich OpenTelemetry activity with search request details
        ActivityEnricher.EnrichSearchActivity(Activity.Current, request);

        // 2. Generate embedding (with Polly resilience via ResilientEmbeddingService)
        _logger.LogInformation("Cache MISS: hash={Hash} — generating embedding", queryHash);
        var vector = await _embeddingService.GenerateEmbeddingAsync(request.Query, cancellationToken);

        // 3. Query FAISS shard (with Polly resilience via ResilientIndexService)
        var grpcRequest = new Protos.SearchRequest
        {
            TopK = request.TopK,
            ShardKey = shardKey,
            Nprobe = request.Nprobe ?? _faissOptions.DefaultNprobe
        };
        grpcRequest.QueryVector.AddRange(vector);

        var grpcResponse = await _indexService.SearchAsync(grpcRequest, cancellationToken);

        // 4. Map results
        var results = grpcResponse.Results.Select(r => new SearchResultItem
        {
            Id = r.Id,
            Score = r.Score,
            Metadata = string.IsNullOrEmpty(r.MetadataJson)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(r.MetadataJson) ?? []
        }).ToList();

        totalSw.Stop();

        var response = new SearchResponse
        {
            Results = results,
            ShardKey = grpcResponse.ShardKey,
            SearchLatencyMs = grpcResponse.SearchLatencyMs,
            TotalLatencyMs = totalSw.Elapsed.TotalMilliseconds,
            CacheHit = false,
            QueryHash = queryHash
        };

        // 5. Populate cache (fire-and-forget — don't delay the response)
        _ = Task.Run(() => _cacheService.SetAsync(queryHash, response, cancellationToken: CancellationToken.None));

        // Enrich OpenTelemetry activity with search response details
        ActivityEnricher.EnrichSearchActivity(Activity.Current, request, response);

        _logger.LogInformation(
            "Search complete: hash={Hash}, results={Count}, embeddingMs+searchMs+totalMs={Total:F1}",
            queryHash, results.Count, response.TotalLatencyMs);

        return response;
    }
}
