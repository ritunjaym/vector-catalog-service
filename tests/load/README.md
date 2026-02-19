# Load Tests

k6 load test suite for the Vector Catalog API.

## Prerequisites

- [k6](https://k6.io/docs/get-started/installation/) v0.49+
- `docker compose up -d` — all 6 services healthy

```bash
k6 version            # verify
docker compose ps     # verify all healthy
```

## Scripts

| Script | Description | Duration |
|--------|-------------|----------|
| `health_load.js` | GET /health/live — ASP.NET Core baseline | ~50s |
| `search_load.js` | POST /api/v1/search — full pipeline | ~3m |

## Running

```bash
# Baseline (health endpoint)
k6 run tests/load/health_load.js

# Full search load test (with JSON output for archiving)
k6 run tests/load/search_load.js \
  --out json=tests/load/results/search_results.json
```

Override the base URL for non-local environments:

```bash
k6 run tests/load/search_load.js --env BASE_URL=http://my-aks-cluster/api
```

## Interpreting Results

### Without a loaded FAISS index

`POST /api/v1/search` will return HTTP 500 (gRPC call to the sidecar fails —
sidecar starts but has no `.index` files until data is ingested via
`scripts/build_faiss_index.py`). The test still measures:

- **C# API framework latency**: routing, middleware chain, Polly circuit breaker,
  serialization — everything before FAISS
- **Throughput ceiling**: how many req/s the API layer can handle
- **Rate limiting**: requests above the configured permit limit return HTTP 429

This gives a *lower bound on production latency* (production adds embedding +
FAISS search time on top of what you see here).

### With a loaded FAISS index

Results include full embedding generation + ANN search. Compare:

| Scenario | Expected P50 | Expected P99 |
|----------|-------------|-------------|
| Health baseline | < 5ms | < 15ms |
| Search (no index) | < 30ms | < 80ms |
| Search (warm cache) | ~48ms | ~425ms |
| Search (cold, no cache) | ~420ms | ~1200ms |

### Key thresholds (search_load.js)

- `p(95) < 500ms` — SLA target
- `p(99) < 1000ms` — hard ceiling
- `error rate < 1%` — allows for expected 429s at peak

## Results Archive

Raw JSON output is gitignored (`tests/load/results/*.json`).
Summarised numbers live in [`docs/BENCHMARKS.md`](../../docs/BENCHMARKS.md).
