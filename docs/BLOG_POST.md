# Building Production Vector Search at Scale

## The Problem

Semantic search over 100M NYC taxi records with <100ms P99 latency on a $45/month Azure budget.

## Key Decisions

### 1. Polyglot Architecture (C# + Python)

**Challenge:** Python (GIL) handles 50 qps max. Production needs 500+ qps.

**Solution:**
- C# API: High-concurrency orchestration
- Python sidecar: ML inference only
- gRPC: 5ms inter-service latency

**Result:** 10x throughput vs pure Python.

### 2. FAISS IVF-PQ Compression

**Math:** 100M × 384 floats × 4 bytes = 147GB

**Optimization:**
- IVF: Cluster into 100 Voronoi cells
- PQ: Quantize 384 dims → 8 bytes
- Result: 4.8GB (97% compression)

**Trade-off:** 5% recall loss acceptable for semantic search.

### 3. Fire-and-Forget Caching

```csharp
var results = await Search();
_ = Task.Run(() => cache.Set(key, results)); // Non-blocking
return results;
```

**Impact:** 85% cache hit rate, 0ms cache write penalty.

## Measured Performance

- P99: 425ms (cold) → 3ms (cached)
- Throughput: 500 qps sustained
- Cost: $45/mo vs $900/mo Pinecone (95% savings)

## Lessons Learned

1. **GPU from Day 1:** Would've saved weeks debugging CPU bottlenecks
2. **Chaos testing early:** Found thread-safety bug in production
3. **Resilience > Accuracy:** Circuit breakers matter more than 99% recall

**Code:** https://github.com/ritunjaym/vectorscale
