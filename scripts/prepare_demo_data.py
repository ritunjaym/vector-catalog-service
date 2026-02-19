#!/usr/bin/env python3
"""
Prepare demo dataset: 10K NYC taxi trips with embeddings and FAISS index.

This makes the demo reproducible without requiring users to download 500MB+ files.
The resulting FAISS index (~2-3MB) is committed to the repo so subsequent runs
load instantly.

Prerequisites:
  - Sidecar container must be running and healthy:
      docker compose up -d sidecar
      # wait ~60s for model to load, then:
      python3 scripts/prepare_demo_data.py

Usage:
  python3 scripts/prepare_demo_data.py
"""

import os
import sys
import socket
import grpc
import urllib.request
import numpy as np
import pyarrow.parquet as pq
import faiss

# ── Path setup so we can import the sidecar's generated proto stubs ──────────
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(REPO_ROOT, 'sidecar'))

import vector_service_pb2          # noqa: E402
import vector_service_pb2_grpc     # noqa: E402

# ── Configuration ─────────────────────────────────────────────────────────────
SIDECAR_ADDR    = os.getenv("SIDECAR_ADDR", "localhost:50051")
TAXI_DATA_URL   = "https://d37ci6vzurychx.cloudfront.net/trip-data/yellow_tripdata_2023-01.parquet"
RAW_FILE        = os.path.join(REPO_ROOT, "data", "raw", "yellow_tripdata_2023-01.parquet")
DEMO_FILE       = os.path.join(REPO_ROOT, "data", "demo", "taxi_trips_10k.parquet")
INDEX_DIR       = os.path.join(REPO_ROOT, "data", "indexes")
INDEX_FILE      = os.path.join(INDEX_DIR, "nyc_taxi_2023.index")
SAMPLE_SIZE     = 10_000
RANDOM_SEED     = 42
BATCH_SIZE      = 256   # texts per gRPC batch call


# ── Step 1: Download and sample ───────────────────────────────────────────────

def download_sample() -> str:
    """Download Jan 2023 yellow-taxi parquet and extract 10K rows."""
    print("Step 1: Downloading NYC taxi data sample...")

    os.makedirs(os.path.dirname(RAW_FILE), exist_ok=True)

    if not os.path.exists(RAW_FILE):
        print(f"  Downloading {TAXI_DATA_URL}")
        print("  (This may take 1-2 minutes for the ~500MB file)")

        def report(block, block_size, total):
            downloaded = block * block_size
            if total > 0:
                pct = min(100, downloaded * 100 // total)
                mb  = downloaded / 1_048_576
                print(f"\r  {pct}% ({mb:.0f} MB)", end="", flush=True)

        urllib.request.urlretrieve(TAXI_DATA_URL, RAW_FILE, reporthook=report)
        print()
        print(f"  Downloaded to {RAW_FILE}")
    else:
        print(f"  Already exists: {RAW_FILE}")

    print("Step 2: Sampling 10,000 trips...")
    table = pq.read_table(RAW_FILE)
    df = table.to_pandas()

    # Filter to valid trips only
    df = df[
        (df['trip_distance'] > 0.1) &
        (df['fare_amount']   > 2.5) &
        (df['passenger_count'].between(1, 6))
    ].copy()

    df_sample = df.sample(n=SAMPLE_SIZE, random_state=RANDOM_SEED).reset_index(drop=True)

    os.makedirs(os.path.dirname(DEMO_FILE), exist_ok=True)
    df_sample.to_parquet(DEMO_FILE, index=False)
    print(f"  Saved {len(df_sample):,} trips to {DEMO_FILE}")
    return DEMO_FILE


# ── Step 2: Generate embeddings via gRPC sidecar ─────────────────────────────

def _check_sidecar():
    """Verify sidecar is reachable before starting the slow embedding pass."""
    host, port_str = SIDECAR_ADDR.split(":")
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.settimeout(3)
    result = sock.connect_ex((host, int(port_str)))
    sock.close()
    if result != 0:
        print("\n  ERROR: Sidecar not reachable at", SIDECAR_ADDR)
        print("  Make sure it is running and the model is loaded:")
        print("    docker compose up -d sidecar")
        print("    # wait ~60s, then re-run this script")
        sys.exit(1)


def _make_text(row) -> str:
    """Convert a taxi trip row into a natural-language string for embedding."""
    pu         = int(row.get('PULocationID', 0))
    do         = int(row.get('DOLocationID', 0))
    dist       = float(row.get('trip_distance', 0))
    fare       = float(row.get('fare_amount', 0))
    passengers = int(row.get('passenger_count', 1))
    pax        = "passengers" if passengers > 1 else "passenger"
    return (
        f"Yellow taxi trip from zone {pu} to zone {do}, "
        f"{dist:.1f} miles, ${fare:.2f} fare, {passengers} {pax}"
    )


def generate_embeddings(demo_file: str) -> np.ndarray:
    """Call the gRPC sidecar to produce embeddings for all 10K trips."""
    print("Step 3: Generating embeddings via gRPC sidecar...")
    _check_sidecar()

    import pandas as pd
    df = pd.read_parquet(demo_file)
    texts = [_make_text(row) for _, row in df.iterrows()]

    channel = grpc.insecure_channel(SIDECAR_ADDR)
    stub    = vector_service_pb2_grpc.EmbeddingServiceStub(channel)

    all_embeddings = []
    total = len(texts)

    for start in range(0, total, BATCH_SIZE):
        batch = texts[start : start + BATCH_SIZE]
        request  = vector_service_pb2.EmbeddingBatchRequest(texts=batch)
        response = stub.GenerateEmbeddingBatch(request)
        for emb in response.embeddings:
            all_embeddings.append(emb.vector)  # field name is `vector`
        done = min(start + BATCH_SIZE, total)
        print(f"  {done}/{total} embeddings generated", end="\r", flush=True)

    print()
    embeddings = np.array(all_embeddings, dtype='float32')
    print(f"  Generated {embeddings.shape[0]:,} embeddings, dim={embeddings.shape[1]}")
    return embeddings


# ── Step 3: Build FAISS IVF-PQ index ─────────────────────────────────────────

def build_faiss_index(embeddings: np.ndarray) -> str:
    """Build a compact IVF-PQ index suitable for 10K vectors."""
    print("Step 4: Building FAISS index...")

    # L2-normalise so cosine ≈ L2 distance on the unit sphere
    faiss.normalize_L2(embeddings)

    n, d = embeddings.shape   # 10000, 384

    # For 10K vectors: nlist=32 gives ~300 vectors/cell (√10K ≈ 100, but 32
    # is safer for training), m=8 subvectors × 8 bits = 1 byte/subvector
    nlist = 32
    m     = 8
    nbits = 8

    quantizer = faiss.IndexFlatL2(d)
    index     = faiss.IndexIVFPQ(quantizer, d, nlist, m, nbits)

    print(f"  Training IVF{nlist},PQ{m}×{nbits} on {n:,} vectors...")
    index.train(embeddings)

    print(f"  Adding {n:,} vectors...")
    index.add(embeddings)
    index.nprobe = 4    # search 4 cells by default — good recall for 10K

    os.makedirs(INDEX_DIR, exist_ok=True)
    faiss.write_index(index, INDEX_FILE)

    size_mb = os.path.getsize(INDEX_FILE) / 1_048_576
    print(f"  FAISS index saved: {INDEX_FILE}")
    print(f"    Vectors : {index.ntotal:,}")
    print(f"    Clusters: {nlist}")
    print(f"    Size    : {size_mb:.1f} MB")
    return INDEX_FILE


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    print("=" * 68)
    print("NYC Taxi Demo Data Preparation")
    print("=" * 68)
    print()

    if os.path.exists(INDEX_FILE):
        print(f"Demo index already exists: {INDEX_FILE}")
        print("Delete it to regenerate, or proceed to the next step.")
        print()
        print("Next step: restart the sidecar so it discovers the index:")
        print("  docker compose restart sidecar")
        print("  # wait ~60s, then:")
        print("  curl -X POST http://localhost:8080/api/v1/search \\")
        print('    -H "Content-Type: application/json" \\')
        print("    -d '{\"query\":\"JFK to Manhattan\",\"topK\":5}'")
        return

    try:
        demo_file  = download_sample()
        embeddings = generate_embeddings(demo_file)
        build_faiss_index(embeddings)

        print()
        print("=" * 68)
        print("Demo data preparation complete!")
        print("=" * 68)
        print()
        print("Next steps:")
        print("  1. Restart sidecar so it picks up the new index:")
        print("       docker compose restart sidecar")
        print("  2. Wait ~60s for model reload, then search:")
        print("       curl -X POST http://localhost:8080/api/v1/search \\")
        print('         -H "Content-Type: application/json" \\')
        print("         -d '{\"query\":\"JFK to Manhattan\",\"topK\":5}'")
        print()
        print(f"  Index file (commit this to the repo): {INDEX_FILE}")

    except Exception as exc:
        import traceback
        print(f"\nError: {exc}")
        traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":
    main()
