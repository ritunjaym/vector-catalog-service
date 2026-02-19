using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace VectorCatalog.Api.Models;

// ── Request / Response DTOs ───────────────────────────────────────────────────

public class SearchRequest
{
    [Required]
    [StringLength(2000, MinimumLength = 1)]
    public string Query { get; set; } = string.Empty;

    [Range(1, 100)]
    public int TopK { get; set; } = 10;

    public string? ShardKey { get; set; }

    [Range(1, 256)]
    public int? Nprobe { get; set; }
}

public class SearchResponse
{
    public IReadOnlyList<SearchResultItem> Results { get; set; } = [];
    public string ShardKey { get; set; } = string.Empty;
    public double SearchLatencyMs { get; set; }
    public double TotalLatencyMs { get; set; }
    public bool CacheHit { get; set; }
    public string QueryHash { get; set; } = string.Empty;
}

public class SearchResultItem
{
    public long Id { get; set; }
    public float Score { get; set; }
    public Dictionary<string, object?> Metadata { get; set; } = [];
}

// ── Internal models ───────────────────────────────────────────────────────────

public class EmbeddingResult
{
    public float[] Vector { get; set; } = [];
    public int Dimension { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public double LatencyMs { get; set; }
}

public class IndexShardInfo
{
    public string ShardKey { get; set; } = string.Empty;
    public long TotalVectors { get; set; }
    public int Dimension { get; set; }
    public string IndexType { get; set; } = string.Empty;
    public bool IsTrained { get; set; }
    public long IndexSizeBytes { get; set; }
}
