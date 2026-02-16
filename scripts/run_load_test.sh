#!/bin/bash
# Quick runner: starts stack, waits for health, runs k6, prints summary.
set -e

echo "=== Vector Catalog Load Test ==="
echo ""

BASE_URL="${BASE_URL:-http://localhost:8080}"

# Check API is up
echo "Checking API health at $BASE_URL..."
for i in {1..30}; do
    if curl -sf "$BASE_URL/health/live" > /dev/null 2>&1; then
        echo "✅ API is up"
        break
    fi
    echo "  Waiting for API... ($i/30)"
    sleep 2
done

# Run k6
echo ""
echo "Starting k6 load test..."
k6 run \
    -e BASE_URL="$BASE_URL" \
    --out json=scripts/load_test_results.json \
    scripts/load_test.js

echo ""
echo "Results written to scripts/load_test_results.json"
