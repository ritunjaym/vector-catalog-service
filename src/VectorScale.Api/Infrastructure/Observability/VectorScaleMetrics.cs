using System.Diagnostics.Metrics;

namespace VectorScale.Api.Infrastructure.Observability;

/// <summary>
/// Custom application metrics exposed via Prometheus.
/// Uses .NET Meter API (OTel-native) — avoids raw Prometheus client coupling.
/// </summary>
public sealed class VectorScaleMetrics : IDisposable
{
    public const string MeterName = "VectorScale.Api";

    private readonly Meter _meter;

    // Search latency histogram — measures end-to-end per-request latency
    public readonly Histogram<double> SearchLatency;

    // Embedding latency — measures just the gRPC embedding call
    public readonly Histogram<double> EmbeddingLatency;

    // Cache counters — used to compute hit ratio in Prometheus
    public readonly Counter<long> CacheHits;
    public readonly Counter<long> CacheMisses;

    // Active searches in-flight
    public readonly UpDownCounter<long> ActiveSearches;

    // Circuit breaker state (1 = open/degraded, 0 = closed/healthy)
    public readonly ObservableGauge<int> CircuitBreakerState;

    private volatile int _circuitBreakerOpen = 0;

    public VectorScaleMetrics()
    {
        _meter = new Meter(MeterName, "0.2.0");

        SearchLatency = _meter.CreateHistogram<double>(
            name: "vectorscale_search_duration_ms",
            unit: "ms",
            description: "End-to-end search request duration");

        EmbeddingLatency = _meter.CreateHistogram<double>(
            name: "vectorscale_embedding_duration_ms",
            unit: "ms",
            description: "gRPC embedding generation duration");

        CacheHits = _meter.CreateCounter<long>(
            name: "vectorscale_cache_hits_total",
            description: "Number of Redis cache hits");

        CacheMisses = _meter.CreateCounter<long>(
            name: "vectorscale_cache_misses_total",
            description: "Number of Redis cache misses");

        ActiveSearches = _meter.CreateUpDownCounter<long>(
            name: "vectorscale_active_searches",
            description: "Number of searches currently in-flight");

        CircuitBreakerState = _meter.CreateObservableGauge<int>(
            name: "vectorscale_circuit_breaker_open",
            observeValue: () => _circuitBreakerOpen,
            description: "1 if circuit breaker is open (degraded), 0 if closed (healthy)");
    }

    public void SetCircuitBreakerOpen(bool isOpen) =>
        Interlocked.Exchange(ref _circuitBreakerOpen, isOpen ? 1 : 0);

    public void Dispose() => _meter.Dispose();
}
