/**
 * k6 Health Endpoint Baseline
 *
 * Hits GET /health/live at 200 VUs for 30s.
 * Measures pure ASP.NET Core framework throughput (routing + middleware only).
 * Use this as an upper-bound baseline: the search endpoint cannot be faster than this.
 *
 * Usage:
 *   k6 run tests/load/health_load.js
 */

import http from 'k6/http';
import { check } from 'k6';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';

export const options = {
  stages: [
    { duration: '10s', target: 200 },
    { duration: '30s', target: 200 },
    { duration: '10s', target: 0   },
  ],
  thresholds: {
    'http_req_duration': ['p(95)<50', 'p(99)<100'],
    'http_req_failed':   ['rate<0.001'],
  },
};

export default function () {
  const res = http.get(`${BASE_URL}/health/live`, { timeout: '2s' });
  check(res, {
    'status 200': (r) => r.status === 200,
    'body is Healthy': (r) => r.body === 'Healthy',
  });
}
