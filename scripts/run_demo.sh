#!/usr/bin/env bash
# =============================================================================
# Vector Catalog Service — One-Command Demo
#
# Usage:
#   ./scripts/run_demo.sh
#
# What this does:
#   1. Starts all services (API, sidecar, Redis, Jaeger, Prometheus)
#   2. Generates demo data on first run (~5 min: downloads 10K trips, embeds, indexes)
#   3. Loads the FAISS index into the sidecar
#   4. Runs two live searches and shows cache-hit on the second
# =============================================================================
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

INDEX_FILE="data/indexes/nyc_taxi_2023.index"

# ── Colour helpers ─────────────────────────────────────────────────────────────
green()  { printf "\033[32m%s\033[0m\n" "$*"; }
yellow() { printf "\033[33m%s\033[0m\n" "$*"; }
red()    { printf "\033[31m%s\033[0m\n" "$*"; }
bold()   { printf "\033[1m%s\033[0m\n"  "$*"; }

# ── Dependency check ──────────────────────────────────────────────────────────
command -v docker  >/dev/null 2>&1 || { red "Error: docker not found"; exit 1; }
command -v python3 >/dev/null 2>&1 || { red "Error: python3 not found"; exit 1; }

echo ""
bold "=========================================="
bold " Vector Catalog Service — Quick Demo"
bold "=========================================="
echo ""

# ── Step 1: Start all services ────────────────────────────────────────────────
yellow "Step 1: Starting services..."
docker compose up -d
green "  Services started"
echo ""

# ── Step 2: Wait for sidecar model load ──────────────────────────────────────
yellow "Step 2: Waiting for sidecar (sentence-transformers model load, up to 90s)..."
for i in $(seq 1 18); do
    if docker logs vector-catalog-sidecar 2>&1 | grep -q "gRPC server is listening"; then
        green "  Sidecar ready (${i}×5s)"
        break
    fi
    if [ "$i" -eq 18 ]; then
        red "  Sidecar did not start in 90s. Check: docker logs vector-catalog-sidecar"
        exit 1
    fi
    printf "  Waiting... %ds\r" "$((i * 5))"
    sleep 5
done
echo ""

# ── Step 3: Generate demo data (first run only) ──────────────────────────────
if [ ! -f "$INDEX_FILE" ]; then
    yellow "Step 3: Generating demo data (first run only, ~5 minutes)..."
    echo ""
    echo "  Installing Python dependencies..."
    pip3 install -q pyarrow pandas numpy faiss-cpu grpcio protobuf 2>/dev/null || \
        pip install -q pyarrow pandas numpy faiss-cpu grpcio protobuf 2>/dev/null
    echo ""
    python3 scripts/prepare_demo_data.py
    echo ""
else
    green "Step 3: Demo index already exists (${INDEX_FILE})"
    echo ""
fi

# ── Step 4: Restart sidecar so it discovers the index ────────────────────────
yellow "Step 4: Restarting sidecar to load FAISS index..."
docker compose restart sidecar >/dev/null
printf "  Waiting for sidecar to reload..."
for i in $(seq 1 18); do
    sleep 5
    if docker logs vector-catalog-sidecar 2>&1 | grep -q "nyc_taxi_2023.*loaded"; then
        echo ""
        green "  Index loaded (shard: nyc_taxi_2023)"
        break
    fi
    printf "."
    if [ "$i" -eq 18 ]; then
        echo ""
        red "  Sidecar did not reload in 90s."
        echo "  Check: docker logs vector-catalog-sidecar"
        exit 1
    fi
done
echo ""

# ── Step 5: Wait for API ──────────────────────────────────────────────────────
yellow "Step 5: Waiting for API..."
for i in $(seq 1 12); do
    if curl -sf http://localhost:8080/health/live >/dev/null 2>&1; then
        green "  API ready"
        break
    fi
    if [ "$i" -eq 12 ]; then
        red "  API did not start. Check: docker compose logs api"
        exit 1
    fi
    sleep 5
done
echo ""

# ── Step 6: Live search demo ──────────────────────────────────────────────────
bold "=========================================="
bold " Demo: Running live search queries"
bold "=========================================="
echo ""

echo "Query 1 (cold — cache miss expected):"
bold '  "taxi from JFK airport to Manhattan"'
echo ""
curl -s -X POST http://localhost:8080/api/v1/search \
  -H "Content-Type: application/json" \
  -d '{"query":"taxi from JFK airport to Manhattan","topK":5}' \
  | python3 -m json.tool 2>/dev/null || echo "(raw response above)"
echo ""

sleep 1

echo "Query 2 (same query — cache hit expected, ~3ms):"
bold '  "taxi from JFK airport to Manhattan"'
echo ""
curl -s -X POST http://localhost:8080/api/v1/search \
  -H "Content-Type: application/json" \
  -d '{"query":"taxi from JFK airport to Manhattan","topK":5}' \
  | python3 -m json.tool 2>/dev/null || echo "(raw response above)"
echo ""

# ── Done ──────────────────────────────────────────────────────────────────────
bold "=========================================="
green " Demo complete!"
bold "=========================================="
echo ""
echo "Try more queries:"
echo "  curl -X POST http://localhost:8080/api/v1/search \\"
echo "    -H 'Content-Type: application/json' \\"
echo "    -d '{\"query\":\"short trip in Midtown Manhattan\",\"topK\":5}'"
echo ""
echo "Explore observability:"
echo "  Swagger UI   : http://localhost:8080/swagger"
echo "  Jaeger Traces: http://localhost:16686"
echo "  Prometheus   : http://localhost:9090"
echo "  MinIO Console: http://localhost:9001  (minioadmin / minioadmin)"
echo ""
