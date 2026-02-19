/**
 * k6 load test for Vector Catalog Service.
 *
 * Targets (Week 2 baseline with warm cache):
 *   P50 < 200ms
 *   P99 < 500ms
 *   Error rate < 1%
 *
 * Run:
 *   # Start stack first: docker compose up -d
 *   k6 run scripts/load_test.js
 *
 *   # With custom target URL:
 *   k6 run -e BASE_URL=http://localhost:8080 scripts/load_test.js
 */
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';

// Custom metrics
const cacheHits = new Counter('cache_hits');
const cacheMisses = new Counter('cache_misses');
const cacheHitRate = new Rate('cache_hit_rate');
const searchLatency = new Trend('search_latency_ms', true);

// Test queries — realistic NYC Taxi search queries
const QUERIES = [
    "taxi ride from JFK airport to Manhattan",
    "yellow cab pickup midtown rush hour",
    "short trip Brooklyn to Queens fare estimate",
    "late night ride manhattan downtown",
    "airport transfer LaGuardia to hotel",
    "taxi from Grand Central Station",
    "trip distance over 10 miles",
    "yellow taxi low fare trip",
    "passenger count single rider taxi",
    "taxi tip amount high fare",
];

export const options = {
    stages: [
        { duration: '30s', target: 10 },   // ramp up
        { duration: '60s', target: 50 },   // sustained load
        { duration: '30s', target: 100 },  // peak
        { duration: '30s', target: 0 },    // ramp down
    ],
    thresholds: {
        // Week 2 targets
        http_req_duration: ['p(50)<200', 'p(99)<500'],
        http_req_failed: ['rate<0.01'],     // < 1% errors
        cache_hit_rate: ['rate>0.7'],       // > 70% cache hit after warmup
    },
};

export function setup() {
    // Warm the cache with all queries before the real test
    console.log('Warming cache...');
    for (const query of QUERIES) {
        http.post(`${BASE_URL}/api/v1/search`,
            JSON.stringify({ query, topK: 10 }),
            { headers: { 'Content-Type': 'application/json' } });
    }
    console.log('Cache warm — starting load test');
}

export default function () {
    const query = QUERIES[Math.floor(Math.random() * QUERIES.length)];
    const payload = JSON.stringify({ query, topK: 10 });

    const res = http.post(`${BASE_URL}/api/v1/search`, payload, {
        headers: {
            'Content-Type': 'application/json',
            'X-Correlation-ID': `k6-${__VU}-${__ITER}`,
        },
        timeout: '5s',
    });

    // Validate response
    const success = check(res, {
        'status is 200': (r) => r.status === 200,
        'has results': (r) => {
            try {
                const body = JSON.parse(r.body);
                return Array.isArray(body.results);
            } catch { return false; }
        },
        'has query hash': (r) => {
            try {
                const body = JSON.parse(r.body);
                return body.queryHash && body.queryHash.length > 0;
            } catch { return false; }
        },
    });

    if (res.status === 200) {
        try {
            const body = JSON.parse(res.body);
            searchLatency.add(body.totalLatencyMs || res.timings.duration);
            if (body.cacheHit) {
                cacheHits.add(1);
                cacheHitRate.add(true);
            } else {
                cacheMisses.add(1);
                cacheHitRate.add(false);
            }
        } catch (_) {}
    }

    sleep(Math.random() * 0.5); // 0-500ms think time
}
