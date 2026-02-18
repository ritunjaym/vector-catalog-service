# Performance Benchmarks

---

## Measured Results — Local Docker Compose (2026-02-18)

> **What was tested**: k6 v1.6.1 against a live Docker Compose stack with all 6 services
> healthy. No FAISS index was loaded (data ingestion requires a pre-built `.index` file
> via `scripts/build_faiss_index.py`). Two endpoints were tested:
>
> 1. `GET /health/live` — pure ASP.NET Core framework throughput (no backend dependency)
> 2. `POST /api/v1/search` — full API pipeline including Polly resilience layer and gRPC
>    call to the sidecar (sidecar responds with `INTERNAL` because no index is loaded)
>
> The search results below **reflect API-layer performance only** — not FAISS search
> latency. They demonstrate the Polly circuit-breaker behaviour and provide a lower bound
> on total request latency in production (production adds embedding + FAISS time on top).
>
> **To reproduce:** `docker compose up -d && k6 run tests/load/search_load.js`

### Test Environment

| Parameter | Value |
|-----------|-------|
| Machine | Apple M2, 8 GB RAM |
| OS | macOS (Darwin 25.3.0) |
| Docker Desktop | Server 29.2.0, 8 CPUs, 3.83 GiB RAM allocated |
| k6 version | v1.6.1 |
| Stack | API + Sidecar + Redis + MinIO + Prometheus + Jaeger |
| FAISS index loaded | No (sidecar healthy, no `.index` files) |

---

### Baseline: GET /health/live (200 VUs, 50s)

```
  ✓ http_req_duration  p(95)=31.32ms   [threshold: <50ms]  PASS
  ✓ http_req_failed    rate=0.00%       [threshold: <0.1%]  PASS

  http_req_duration: avg=9.15ms  med=7.01ms  p(90)=13.33ms  p(95)=31.32ms  p(99)=57.41ms
  http_reqs........: 869,817  @ 17,396 req/s
  vus_max..........: 200
```

| Metric | Value |
|--------|-------|
| P50 | 7ms |
| P90 | 13ms |
| P95 | 31ms |
| P99 | 57ms |
| Throughput | **17,396 req/s** at 200 VUs |
| Error rate | 0.00% |
| Total requests | 869,817 in 50s |

The health endpoint measures raw ASP.NET Core + Kestrel overhead. At 17K+ req/s with
P99=57ms under 200 concurrent users, the framework layer introduces minimal overhead.
This is the **upper bound on throughput** — search requests cannot be faster than this.

---

### Search API: POST /api/v1/search (10→50→100 VUs, 3m)

No FAISS index was loaded, so every request hits the Polly resilience layer before
returning HTTP 500 with an RFC 7807 ProblemDetails body. This exercises the real code
path through: routing → rate limiter → validation → cache lookup → gRPC → Polly.

```
  ✗ http_req_duration  p(95)=5s         [threshold: <500ms] FAIL (expected — no index)
  ✗ http_req_failed    rate=100%        [threshold: <1%]    FAIL (expected — no index)

  http_req_duration: avg=3.41s  med=4.3s  p(90)=5s  p(95)=5s  (k6 5s client timeout)
  http_reqs........: 2,306  @ 12.8 req/s
  vus_max..........: 100
```

**Server-side ASP.NET histogram** (`/metrics`, for the 1,270 requests that returned
before k6's 5s client timeout):

| Metric | Value | Notes |
|--------|-------|-------|
| Total completed (HTTP 500) | 1,270 | Received RFC 7807 ProblemDetails |
| Client-timed-out (no response) | 1,036 | k6 5s timeout exceeded |
| Avg server-side duration | **2,100ms** | `sum=2668s / count=1270` |
| Fast-fail responses (≤250ms) | 58 (4.6%) | Circuit breaker already open → BrokenCircuitException |
| Slow retries (250ms–2.5s) | 722 (56.8%) | Polly retry: 3× with 200/400/800ms backoff |
| Near-timeout (2.5s–5s) | 486 (38.3%) | Circuit closed again after 30s break, retrying |

**Why requests take 1–5s (Polly in action):**

The `ResilientIndexService` wraps gRPC calls with `ResiliencePolicies.GetCombinedGrpcPolicy`:
1. **Retry policy**: 3 retries on `INTERNAL`/`UNAVAILABLE` with exponential backoff
   (200ms → 400ms → 800ms + jitter). Each individual gRPC attempt fails immediately
   (sidecar returns `INTERNAL` synchronously), but Polly waits ~1.4s total for 3 retries.
2. **Circuit breaker**: opens after 50% failure rate over 5+ requests in a 10s window.
   Once open, calls throw `BrokenCircuitException` immediately (< 1ms). This accounts
   for the 58 fast-fail responses (≤250ms).
3. **Timeout policy**: 5s overall cap. Requests that exhaust retries AND catch the circuit
   re-closing during the 30s break period may hit this limit.
4. **RFC 7807 response**: `app.UseExceptionHandler()` converts all unhandled exceptions
   to `application/problem+json` — confirmed working in every 500 response.

**Throughput note**: 12.8 req/s reflects concurrency limited by Polly retry delays
(each VU blocks for ~1.4s per retry cycle). With a loaded FAISS index (fast success path),
the same API layer sustained **52 req/s** on a synthetic ~100K-vector index (see section
below) and is projected at **500–800 req/s** on AKS with horizontal scaling.

---

### Cache Performance

Not measurable in this run — the Redis cache-aside check in `SearchService` is reached,
but the gRPC call to FAISS fails before any results are written to cache. Redis health
check: `GET /health/ready` → 200 OK (Redis dependency healthy).

**Cache round-trip validated separately** via integration tests:
`Search_SecondIdenticalRequest_ReturnsCacheHit` passes consistently (12/12 tests green).

---

### Observability Confirmed

- `GET /metrics` exposes OpenTelemetry Prometheus format on port 8080 ✅
- `http_server_request_duration_seconds` histogram populated for all routes ✅
- `error_type="Grpc.Core.RpcException"` label on 500 responses confirms gRPC error
  propagation is instrumented ✅
- Jaeger traces confirm parent-child span relationships (API → gRPC sidecar) ✅
- Note: `prometheus.yml` scrape target points to port 8081 (incorrect); fix pending.
  Direct scrape via `curl localhost:8080/metrics` works correctly.

---

---

## Reference: Synthetic FAISS Index Results (Prior Measurement)

> These numbers were measured against a **synthetic development FAISS index**
> (IVF100, PQ8, ~100K vectors on NYC Taxi 2023-01). The "100M vectors" figure describes
> the **target production scale**, not the test dataset. See the Reproducibility Note
> at the top of the original section for full context.

### Latency (P50/P95/P99) — Warm Cache

| Metric | Target | Achieved |
|--------|--------|----------|
| P50 | < 200ms | **152ms** |
| P95 | < 400ms | **380ms** |
| P99 | < 500ms | **425ms** |
| Max | N/A | 890ms |

### Throughput

| Metric | Value |
|--------|-------|
| Avg throughput | 52 req/s (warm cache) |
| Peak throughput | 98 req/s at 100 VUs |
| Error rate | 0.08% (6 rate-limited 429s) |
| Total requests | 7,823 over 150s |

### Cache

| Metric | Value |
|--------|-------|
| Cache hit rate | 85.3% (6,674 hits / 1,149 misses) |
| Avg cache hit latency | 48ms |
| Avg cache miss latency | 380ms (embedding 120ms + FAISS 260ms) |

### k6 Summary Output

```
     cache_hit_rate.............: 85.30% ✓ 6674   ✗ 1149
     http_req_duration..........: avg=152.2ms min=18ms  med=48ms   max=890ms   p(95)=380ms  p(99)=425ms
     http_req_failed............: 0.08%  ✓ 6      ✗ 7817
     http_reqs..................: 7823   52.1/s
     vus_max....................: 100    min=100  max=100
```

---

## AKS Production Projections

> These are linear extrapolations based on FAISS's documented O(nlist) sub-linear
> search complexity and the measured single-shard latency above. Full-scale validation
> (IVF4096, PQ32, 100M+ vectors) requires dedicated infrastructure and is pending.

### Expected Performance (3 API + 5 Sidecar pods)

| Metric | Projected Value | Assumptions |
|--------|-----------------|-------------|
| P50 Latency | **100–150ms** | Warm cache, load balanced |
| P99 Latency | **300–400ms** | Occasional cache misses |
| Throughput | **500–800 qps** | 3 API pods × 150 qps + 5 Sidecar pods |
| Cache Hit Rate | **80–90%** | Redis cluster with 4Gi cache |
| Max Concurrent Users | **1,000+** | HPA scaling to 10 API pods |

### Scaling Strategy

| Load (qps) | API Pods | Sidecar Pods | Redis Size | Expected P99 |
|------------|----------|--------------|------------|--------------|
| < 100 | 2 | 3 | 2Gi | 200ms |
| 100–300 | 3–5 | 5 | 4Gi | 300ms |
| 300–600 | 5–8 | 7 | 8Gi | 350ms |
| 600–1000 | 8–10 | 10 | 16Gi | 400ms |

### Cost Analysis (Azure — East US)

| Component | SKU | Monthly Cost |
|-----------|-----|--------------|
| AKS Control Plane | Standard | $73 |
| Worker Nodes (3× D4s_v5) | 4 vCPU, 16 GB | $420 |
| Managed Disk (50Gi Premium SSD) | P10 | $19 |
| Load Balancer | Standard | $18 |
| Bandwidth (1 TB egress) | Data Transfer | $87 |
| **Total** | — | **$617/month** |

---

## Optimization Opportunities

### Short-term
1. **Fix Prometheus scrape target**: change `api:8081` → `api:8080` in `prometheus.yml`
2. **Embedding caching**: cache embeddings by query hash → −30% P99 on cache misses
3. **gRPC connection pooling**: increase pool from 10 → 50 → +15% throughput under load

### Mid-term
1. **FAISS index tuning**: IVF4096→IVF8192, PQ32→PQ24 → −20% FAISS latency
2. **GPU acceleration**: NVIDIA T4 for embedding (120ms → 15ms); +$350/month on AKS
3. **Multi-shard fan-out**: parallel queries across 3 shards → +50% throughput

### Long-term
1. **HNSW index**: better recall at similar latency; shardable to 1B+ vectors
2. **Real-time ingestion**: Kafka consumer for live NYC Taxi data
3. **Query prefetching**: ML model to predict next search sequences

---

**Last Updated**: 2026-02-18
**k6 version**: v1.6.1
**Load test scripts**: `tests/load/` (see `tests/load/README.md`)
**Next benchmark**: full-pipeline run after `scripts/build_faiss_index.py` completes
