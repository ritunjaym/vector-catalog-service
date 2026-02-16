using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VectorCatalog.Api.Configuration;
using VectorCatalog.Api.Models;
using VectorCatalog.Api.Protos;
using SearchRequest = VectorCatalog.Api.Models.SearchRequest;
using SearchResponse = VectorCatalog.Api.Models.SearchResponse;

namespace VectorCatalog.Api.Services;

public class SearchService : ISearchService
{
    private readonly IEmbeddingService _embeddingService;
    private readonly ICacheService _cacheService;
    private readonly IndexService.IndexServiceClient _indexClient;
    private readonly FaissOptions _faissOptions;
    private readonly ILogger<SearchService> _logger;

    public SearchService(
        IEmbeddingService embeddingService,
        ICacheService cacheService,
        IndexService.IndexServiceClient indexClient,
        IOptions<VectorCatalogOptions> options,
        ILogger<SearchService> logger)
    {
        _embeddingService = embeddingService;
        _cacheService = cacheService;
        _indexClient = indexClient;
        _faissOptions = options.Value.Faiss;
        _logger = logger;
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();

        var shardKey = request.ShardKey ?? _faissOptions.DefaultShardKey;
        var topK = request.TopK;
        var queryHash = _cacheService.ComputeQueryHash(request.Query, topK, shardKey);

        // 1. Check cache
        var cached = await _cacheService.GetAsync<SearchResponse>(queryHash, cancellationToken);
        if (cached is not null)
        {
            cached.CacheHit = true;
            cached.TotalLatencyMs = totalSw.Elapsed.TotalMilliseconds;
            _logger.LogInformation("Cache hit for query hash {Hash}", queryHash);
            return cached;
        }

        // 2. Generate embedding
        _logger.LogInformation("Generating embedding for query (hash={Hash})", queryHash);
        var vector = await _embeddingService.GenerateEmbeddingAsync(request.Query, cancellationToken);

        // 3. Query FAISS index
        var grpcRequest = new Protos.SearchRequest
        {
            TopK = topK,
            ShardKey = shardKey,
            Nprobe = request.Nprobe ?? _faissOptions.DefaultNprobe
        };
        grpcRequest.QueryVector.AddRange(vector);

        var grpcResponse = await _indexClient.SearchIndexAsync(grpcRequest, cancellationToken: cancellationToken);

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

        // 5. Store in cache (fire-and-forget, don't block response)
        _ = _cacheService.SetAsync(queryHash, response, cancellationToken: CancellationToken.None);

        _logger.LogInformation(
            "Search complete: hash={Hash}, results={Count}, totalMs={Total:F1}",
            queryHash, results.Count, response.TotalLatencyMs);

        return response;
    }
}
