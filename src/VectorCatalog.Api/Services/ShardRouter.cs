namespace VectorCatalog.Api.Services;

/// <summary>
/// Routes search queries to the correct FAISS index shard.
///
/// Sharding strategy (explainable in system design interview):
///   - Shard key = logical partition (e.g., "nyc_taxi_2023", "nyc_taxi_2022")
///   - For 100M+ records: partition by year-month ("nyc_taxi_2023-01")
///   - Each shard is an independent FAISS IVF-PQ index loaded in the sidecar
///   - Fan-out search (query multiple shards, merge results) is a Week 3 feature
///
/// Why not hash-based sharding?
///   - Content-based sharding (by time/category) enables index pruning:
///     "find similar rides from 2023" can skip 2022 shards entirely.
///   - Hash sharding distributes load evenly but loses semantic locality.
/// </summary>
public class ShardRouter
{
    private readonly ILogger<ShardRouter> _logger;

    public ShardRouter(ILogger<ShardRouter> logger) => _logger = logger;

    /// <summary>
    /// Determines which shard key to use for a query.
    /// Supports explicit shard key override (for time-scoped queries).
    /// Falls back to default shard if no routing hints provided.
    /// </summary>
    public string ResolveShardKey(string? requestedShardKey, string defaultShardKey)
    {
        if (!string.IsNullOrWhiteSpace(requestedShardKey))
        {
            _logger.LogDebug("Using explicit shard key: {ShardKey}", requestedShardKey);
            return requestedShardKey;
        }

        _logger.LogDebug("No shard key specified — routing to default: {ShardKey}", defaultShardKey);
        return defaultShardKey;
    }

    /// <summary>
    /// For multi-shard fan-out (future Week 3): returns all shard keys to query in parallel.
    /// Currently returns single shard — placeholder for expansion.
    /// </summary>
    public IReadOnlyList<string> ResolveShardKeys(string? requestedShardKey, string defaultShardKey)
    {
        var key = ResolveShardKey(requestedShardKey, defaultShardKey);
        return [key]; // Expand to multiple shards in Week 3
    }
}
