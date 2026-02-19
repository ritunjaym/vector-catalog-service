using System.Diagnostics;
using VectorCatalog.Api.Models;

namespace VectorCatalog.Api.Infrastructure.Observability;

/// <summary>
/// Enriches OpenTelemetry Activity (spans) with custom tags for searchability in Jaeger.
/// These tags enable filtering traces by query length, cache hits, result counts, etc.
/// </summary>
public static class ActivityEnricher
{
    /// <summary>
    /// Enriches search activity with request and optionally response details.
    /// Call twice: once after cache miss with request only, then after response is ready.
    /// </summary>
    public static void EnrichSearchActivity(Activity? activity, SearchRequest request, SearchResponse? response = null)
    {
        if (activity == null) return;

        // Request details (always available)
        activity.SetTag("search.query_length", request.Query.Length);
        activity.SetTag("search.top_k", request.TopK);
        activity.SetTag("search.shard_key", request.ShardKey ?? "default");
        activity.SetTag("search.nprobe", request.Nprobe ?? 0);

        // Response details (available after search completes)
        if (response != null)
        {
            activity.SetTag("search.cache_hit", response.CacheHit);
            activity.SetTag("search.result_count", response.Results.Count);
            activity.SetTag("search.total_latency_ms", response.TotalLatencyMs);
            activity.SetTag("search.search_latency_ms", response.SearchLatencyMs);
            activity.SetTag("search.query_hash", response.QueryHash);
        }
    }

    /// <summary>
    /// Enriches embedding generation activity with text and optionally the resulting vector.
    /// Call twice: once before gRPC call with text only, then after vector is returned.
    /// </summary>
    public static void EnrichEmbeddingActivity(Activity? activity, string text, float[]? vector = null)
    {
        if (activity == null) return;

        activity.SetTag("embedding.text_length", text.Length);

        if (vector != null)
        {
            activity.SetTag("embedding.dimension", vector.Length);
            activity.SetTag("embedding.model", "all-MiniLM-L6-v2");
        }
    }
}
