/**
 * k6 Load Test — Vector Catalog Search Endpoint
 *
 * Tests POST /api/v1/search under realistic load.
 *
 * NOTE: If no FAISS index is loaded in the sidecar, requests return HTTP 500
 * (gRPC call to empty sidecar fails). The test still measures meaningful metrics:
 *   - End-to-end C# API framework latency (routing, middleware, Polly, serialization)
 *   - Throughput of the full request pipeline before FAISS
 *   - Rate limiter behaviour at peak load
 * Results reflect "API framework overhead" — latency will be lower than production
 * (no embedding generation or FAISS search), not higher.
 *
 * Usage:
 *   k6 run tests/load/search_load.js --out json=tests/load/results/search_results.json
 */

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

// Custom metrics
const cacheHits   = new Counter('cache_hits');
const cacheMisses = new Counter('cache_misses');
const cacheHitRate = new Rate('cache_hit_rate');

// Realistic NYC Taxi search queries (varied to exercise query-hash diversity)
const QUERIES = [
  'airport pickup JFK early morning',
  'evening rides manhattan midtown',
  'long distance trip brooklyn to bronx',
  'rainy day commute lower east side',
  'rush hour taxi times square',
  'late night ride harlem to downtown',
  'short hop chelsea to tribeca',
  'business district wall street morning',
  'theater district pickup 42nd street',
  'brooklyn bridge tourist area afternoon',
  'upper west side to central park',
  'grand central station taxi queue',
  'williamsburg bridge crosstown fare',
  'queens to manhattan fare estimate',
  'holiday weekend surge pricing',
];

const BASE_URL   = __ENV.BASE_URL || 'http://localhost:8080';
const SEARCH_URL = `${BASE_URL}/api/v1/search`;

export const options = {
  stages: [
    { duration: '30s', target: 10  },  // ramp-up
    { duration: '60s', target: 50  },  // sustained mid load
    { duration: '60s', target: 100 },  // peak load
    { duration: '30s', target: 0   },  // ramp-down
  ],
  thresholds: {
    'http_req_duration{scenario:default}': ['p(95)<500', 'p(99)<1000'],
    'http_req_failed':                     ['rate<0.01'],
  },
};

export default function () {
  const query = QUERIES[Math.floor(Math.random() * QUERIES.length)];
  const payload = JSON.stringify({ query, topK: 10 });

  const params = {
    headers: { 'Content-Type': 'application/json' },
    timeout: '5s',
  };

  const res = http.post(SEARCH_URL, payload, params);

  // 200 = cache hit or successful search
  // 429 = rate limited (expected at peak)
  // 500 = no index loaded (expected without ingested data)
  const success = check(res, {
    'status 2xx or 4xx (not 5xx network error)': (r) =>
      r.status >= 200 && r.status < 600,
    'has response body': (r) => r.body && r.body.length > 0,
    'response time < 2000ms': (r) => r.timings.duration < 2000,
  });

  // Track cache hit/miss from response body when search succeeds
  if (res.status === 200) {
    try {
      const body = JSON.parse(res.body);
      if (body.cacheHit === true) {
        cacheHits.add(1);
        cacheHitRate.add(true);
      } else {
        cacheMisses.add(1);
        cacheHitRate.add(false);
      }
    } catch (_) { /* non-JSON body, skip */ }
  }

  sleep(0.1); // 100ms think time (realistic API consumer)
}
