# Service Level Agreement (SLA)

## Uptime Target

| Tier | Target | Error Budget (per 30 days) |
|------|--------|---------------------------|
| Search API | **99.9%** | 43.8 minutes/month |
| Embedding Sidecar | 99.5% | 3.65 hours/month |
| Delta Lake ingestion | 99.0% | 7.3 hours/month |

---

## Performance SLOs

| Metric | Target | Alert Threshold | Window |
|--------|--------|-----------------|--------|
| P50 search latency | < 50 ms | — | 5 min |
| P99 search latency | < 500 ms | > 500 ms for 2 min | 5 min |
| P99.9 search latency | < 2 000 ms | — | — |
| Redis cache hit rate | ≥ 85% | < 70% for 5 min | 5 min |
| HTTP 5xx error rate | < 0.1% | > 1% for 1 min | 5 min |
| Circuit breaker open | 0 occurrences | > 0 for 30 s | — |

---

## Incident Definitions

| Severity | Definition | Response Time | Resolution Time |
|----------|-----------|---------------|-----------------|
| **P0 — Critical** | Search API fully unavailable OR circuit breaker open > 5 min | 15 min | 2 hours |
| **P1 — High** | P99 latency > 1 s sustained OR error rate > 5% | 30 min | 4 hours |
| **P2 — Medium** | Cache hit rate < 70% OR P99 > 500 ms for > 10 min | 2 hours | 8 hours |
| **P3 — Low** | Non-critical degradation, single transient errors | Next business day | 48 hours |

---

## Alert Rules

Four Prometheus alerts are defined in [`infra/docker/alert_rules.yml`](../infra/docker/alert_rules.yml):

| Alert | Expression | Duration | Severity |
|-------|-----------|----------|----------|
| `CircuitBreakerOpen` | `vector_catalog_circuit_breaker_state > 0` | 30 s | critical |
| `P99LatencyHigh` | `histogram_quantile(0.99, ...) > 500` | 2 min | warning |
| `CacheHitRateLow` | `vector_catalog_cache_hit_rate < 0.70` | 5 min | warning |
| `ErrorRateHigh` | 5xx rate > 1% (5-min rolling) | 1 min | critical |

---

## Monitoring Links

| Tool | URL |
|------|-----|
| Live health check | `https://vector-catalog-api.politefield-8fe8e6a2.eastus.azurecontainerapps.io/health` |
| Live Prometheus metrics | `https://vector-catalog-api.politefield-8fe8e6a2.eastus.azurecontainerapps.io/metrics` |
| Local Prometheus | `http://localhost:9090` |
| Local Grafana dashboard | `http://localhost:3000` (admin / admin) |
| GitHub Actions (CI) | `https://github.com/ritunjaym/vectorscale/actions` |
| Trivy security scan | `https://github.com/ritunjaym/vectorscale/security` |

---

## Error Budget Burn Rate

With a 99.9% uptime target the monthly error budget is **43.8 minutes**.

| Burn rate | Meaning | Alert within |
|-----------|---------|--------------|
| 1× | Consuming budget at exactly the SLO rate | — |
| 5× | Budget exhausted in ~6 days | P2 |
| 14.4× | Budget exhausted in ~2 hours | P1 |
| 36× | Budget exhausted in ~1 hour | P0 |

---

## Exclusions

- Scheduled maintenance windows (announced ≥ 24 hours in advance, max 4 hours/month)
- Force majeure (Azure region outage, DNS provider failure)
- Client-side network issues outside Azure
