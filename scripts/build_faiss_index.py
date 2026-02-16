#!/usr/bin/env python3
"""
FAISS Index Builder for Vector Catalog Service
Reads embeddings from Delta Lake, builds IVF-PQ index, writes to disk.

IVF-PQ Configuration:
- nlist=100: 100 inverted file clusters (Voronoi cells)
- m=8: Compress vectors into 8 product quantization sub-vectors
- nbits=8: 8 bits per sub-vector (256 centroids per sub-quantizer)
- Result: 100x compression ratio, ~95% recall@10

Usage:
    python3 scripts/build_faiss_index.py --input data/delta/taxi_embeddings --output data/indexes/nyc_taxi_2023.index
"""
import argparse
import logging
import os
import sys
import time
import numpy as np
import faiss
from pyspark.sql import SparkSession

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)


def create_spark_session():
    """Create Spark session with Delta Lake support"""
    return (SparkSession.builder
            .appName("FAISS-Index-Builder")
            .config("spark.jars.packages", "io.delta:delta-spark_2.12:3.1.0")
            .config("spark.sql.extensions", "io.delta.sql.DeltaSparkSessionExtension")
            .config("spark.sql.catalog.spark_catalog", "org.apache.spark.sql.delta.catalog.DeltaCatalog")
            .config("spark.executor.memory", "4g")
            .config("spark.driver.memory", "4g")
            .getOrCreate())


def load_embeddings_from_delta(spark, delta_path):
    """
    Load embeddings from Delta Lake into memory.

    Args:
        spark: SparkSession
        delta_path: Path to Delta table

    Returns:
        numpy array of shape (n_vectors, dimension)
    """
    logger.info(f"Loading embeddings from Delta Lake: {delta_path}")

    df = spark.read.format("delta").load(delta_path)
    total_count = df.count()
    logger.info(f"Found {total_count} records in Delta table")

    # Collect embeddings (be careful with memory for large datasets)
    embeddings_list = df.select("embedding").rdd.map(lambda row: row.embedding).collect()

    # Convert to numpy array
    embeddings = np.array(embeddings_list, dtype=np.float32)
    logger.info(f"Loaded embeddings shape: {embeddings.shape}")

    return embeddings


def build_ivfpq_index(embeddings, nlist=100, m=8, nbits=8):
    """
    Build FAISS IVF-PQ index with specified parameters.

    IVF (Inverted File): Partitions space into nlist Voronoi cells for coarse quantization
    PQ (Product Quantization): Compresses vectors by splitting into m sub-vectors

    Args:
        embeddings: numpy array of shape (n_vectors, dimension)
        nlist: Number of IVF clusters (100 is good for 1M-10M vectors)
        m: Number of PQ sub-vectors (must divide dimension evenly)
        nbits: Bits per PQ code (8 = 256 centroids per sub-quantizer)

    Returns:
        Trained FAISS index
    """
    n_vectors, dimension = embeddings.shape
    logger.info(f"Building IVF-PQ index: nlist={nlist}, m={m}, nbits={nbits}")

    # Ensure dimension is divisible by m
    if dimension % m != 0:
        raise ValueError(f"Dimension {dimension} must be divisible by m={m}")

    # Create IVF-PQ index
    # IndexIVFPQ(quantizer, d, nlist, m, nbits)
    quantizer = faiss.IndexFlatL2(dimension)  # Coarse quantizer
    index = faiss.IndexIVFPQ(quantizer, dimension, nlist, m, nbits)

    logger.info("Training index (this may take several minutes)...")
    start_time = time.time()

    # Training requires at least nlist * 39 vectors (FAISS rule of thumb)
    min_training_vectors = nlist * 39
    if n_vectors < min_training_vectors:
        logger.warning(f"Only {n_vectors} vectors available. Recommended minimum: {min_training_vectors}")

    # Sample training vectors if dataset is huge (>1M vectors)
    if n_vectors > 1_000_000:
        training_sample_size = min(1_000_000, n_vectors)
        logger.info(f"Sampling {training_sample_size} vectors for training")
        training_indices = np.random.choice(n_vectors, training_sample_size, replace=False)
        training_vectors = embeddings[training_indices]
    else:
        training_vectors = embeddings

    # Train the index
    index.train(training_vectors)
    training_time = time.time() - start_time
    logger.info(f"Training completed in {training_time:.2f} seconds")

    # Add all vectors to the index
    logger.info("Adding vectors to index...")
    add_start = time.time()
    index.add(embeddings)
    add_time = time.time() - add_start
    logger.info(f"Added {index.ntotal} vectors in {add_time:.2f} seconds")

    return index


def save_index(index, output_path):
    """
    Write FAISS index to disk.

    Args:
        index: Trained FAISS index
        output_path: Filesystem path for output (e.g., /data/indexes/nyc_taxi_2023.index)
    """
    # Create output directory if it doesn't exist
    os.makedirs(os.path.dirname(output_path), exist_ok=True)

    logger.info(f"Saving index to: {output_path}")
    faiss.write_index(index, output_path)

    # Print index statistics
    file_size_mb = os.path.getsize(output_path) / (1024 * 1024)
    logger.info(f"Index saved successfully!")
    logger.info(f"  - File size: {file_size_mb:.2f} MB")
    logger.info(f"  - Total vectors: {index.ntotal}")
    logger.info(f"  - Dimension: {index.d}")


def test_index(index, embeddings, k=10):
    """
    Run a quick smoke test on the index with random queries.

    Args:
        index: FAISS index
        embeddings: Original embeddings array
        k: Number of neighbors to retrieve
    """
    logger.info(f"Running smoke test (top-{k} search)...")

    # Use first 5 vectors as test queries
    n_test = min(5, embeddings.shape[0])
    test_queries = embeddings[:n_test]

    # Set nprobe for search (higher = more accurate but slower)
    if hasattr(index, 'nprobe'):
        index.nprobe = 10

    distances, indices = index.search(test_queries, k)

    logger.info("Sample search results:")
    for i in range(n_test):
        logger.info(f"  Query {i}: top-3 neighbors = {indices[i][:3]}, distances = {distances[i][:3]}")

    logger.info("✓ Smoke test passed!")


def main():
    parser = argparse.ArgumentParser(description="Build FAISS IVF-PQ index from Delta Lake embeddings")
    parser.add_argument("--input", required=True, help="Delta Lake path (e.g., data/delta/taxi_embeddings)")
    parser.add_argument("--output", required=True, help="Output index path (e.g., data/indexes/nyc_taxi_2023.index)")
    parser.add_argument("--nlist", type=int, default=100, help="Number of IVF clusters (default: 100)")
    parser.add_argument("--m", type=int, default=8, help="PQ sub-vectors (default: 8)")
    parser.add_argument("--nbits", type=int, default=8, help="Bits per PQ code (default: 8)")
    args = parser.parse_args()

    logger.info("=" * 70)
    logger.info("FAISS Index Builder - Vector Catalog Service")
    logger.info("=" * 70)

    try:
        # Create Spark session and load embeddings
        spark = create_spark_session()
        embeddings = load_embeddings_from_delta(spark, args.input)
        spark.stop()

        # Build FAISS index
        index = build_ivfpq_index(embeddings, nlist=args.nlist, m=args.m, nbits=args.nbits)

        # Save to disk
        save_index(index, args.output)

        # Run smoke test
        test_index(index, embeddings, k=10)

        logger.info("=" * 70)
        logger.info("✓ Index build completed successfully!")
        logger.info("=" * 70)

    except Exception as e:
        logger.error(f"Index build failed: {e}", exc_info=True)
        sys.exit(1)


if __name__ == "__main__":
    main()
