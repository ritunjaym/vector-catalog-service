"""
PySpark Ingestion Job: NYC Taxi Data → Embeddings → Delta Lake
Reads parquet files, generates embeddings via gRPC sidecar, writes to Delta.
Supports incremental processing and partitioning by year/month.
"""
import sys
import os
import time
import logging
from datetime import datetime
from pyspark.sql import SparkSession
from pyspark.sql.functions import udf, col, concat_ws, lit, year, month
from pyspark.sql.types import ArrayType, FloatType
import grpc

# Add sidecar protos to path
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '../../sidecar')))
import vector_service_pb2
import vector_service_pb2_grpc

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)

# Configuration
GRPC_HOST = os.getenv('GRPC_HOST', 'localhost')
GRPC_PORT = os.getenv('GRPC_PORT', '50051')
INPUT_PATH = os.getenv('INPUT_PATH', 'data/raw/yellow_tripdata_2023-01.parquet')
DELTA_OUTPUT_PATH = os.getenv('DELTA_OUTPUT_PATH', 'data/delta/taxi_embeddings')
BATCH_SIZE = int(os.getenv('BATCH_SIZE', '1000'))  # Embeddings per gRPC call


def create_embedding_udf():
    """
    Create a PySpark UDF that calls the gRPC embedding service.
    Uses thread-local connection pooling for efficiency.
    """
    import threading
    thread_local = threading.local()

    def get_grpc_stub():
        """Get or create thread-local gRPC stub"""
        if not hasattr(thread_local, 'stub'):
            channel = grpc.insecure_channel(
                f'{GRPC_HOST}:{GRPC_PORT}',
                options=[
                    ('grpc.max_send_message_length', 100 * 1024 * 1024),
                    ('grpc.max_receive_message_length', 100 * 1024 * 1024),
                ]
            )
            thread_local.stub = vector_service_pb2_grpc.EmbeddingServiceStub(channel)
            thread_local.channel = channel
        return thread_local.stub

    def generate_embedding(text):
        """
        Generate embedding for a single text via gRPC.
        Returns list of floats (384 dimensions for all-MiniLM-L6-v2).
        """
        if not text or text.strip() == "":
            return [0.0] * 384  # Return zero vector for empty text

        try:
            stub = get_grpc_stub()
            request = vector_service_pb2.EmbeddingRequest(
                text=text[:512],  # Truncate to prevent token limit issues
                model_name="all-MiniLM-L6-v2"
            )
            response = stub.GenerateEmbedding(request)
            return list(response.vector)
        except Exception as e:
            logger.error(f"Error generating embedding: {e}")
            return [0.0] * 384  # Fallback to zero vector on error

    return udf(generate_embedding, ArrayType(FloatType()))


def create_spark_session():
    """Create Spark session with Delta Lake support"""
    return (SparkSession.builder
            .appName("NYCTaxi-Ingestion-Embedding")
            .config("spark.jars.packages", "io.delta:delta-spark_2.12:3.1.0")
            .config("spark.sql.extensions", "io.delta.sql.DeltaSparkSessionExtension")
            .config("spark.sql.catalog.spark_catalog", "org.apache.spark.sql.delta.catalog.DeltaCatalog")
            .config("spark.sql.shuffle.partitions", "8")
            .config("spark.executor.memory", "4g")
            .config("spark.driver.memory", "2g")
            .getOrCreate())


def create_text_representation(df):
    """
    Concatenate relevant taxi trip columns into a single text field.
    This text will be embedded by the sentence-transformer model.

    Example text: "taxi ride manhattan pickup drop off midtown distance 2.5 miles fare 15 dollars"
    """
    return df.withColumn(
        "text_repr",
        concat_ws(
            " ",
            lit("taxi ride manhattan"),
            col("PULocationID").cast("string"),
            lit("to"),
            col("DOLocationID").cast("string"),
            lit("distance"),
            col("trip_distance").cast("string"),
            lit("miles fare"),
            col("fare_amount").cast("string"),
            lit("dollars")
        )
    )


def main():
    logger.info("=" * 60)
    logger.info("NYC Taxi Ingestion & Embedding Job")
    logger.info("=" * 60)
    logger.info(f"Input Path: {INPUT_PATH}")
    logger.info(f"Delta Output Path: {DELTA_OUTPUT_PATH}")
    logger.info(f"gRPC Endpoint: {GRPC_HOST}:{GRPC_PORT}")
    logger.info(f"Batch Size: {BATCH_SIZE}")

    # Create Spark session
    spark = create_spark_session()
    spark.sparkContext.setLogLevel("WARN")

    try:
        # Read NYC Taxi data from parquet
        logger.info("Reading NYC Taxi data from parquet...")
        df = spark.read.parquet(INPUT_PATH)
        logger.info(f"Loaded {df.count()} records")

        # Data preprocessing
        logger.info("Preprocessing data...")
        df_clean = (df
                    .filter(col("fare_amount") > 0)
                    .filter(col("trip_distance") > 0)
                    .filter(col("trip_distance") < 100)  # Remove outliers
                    .select(
                        col("VendorID"),
                        col("tpep_pickup_datetime"),
                        col("tpep_dropoff_datetime"),
                        col("passenger_count"),
                        col("trip_distance"),
                        col("PULocationID"),
                        col("DOLocationID"),
                        col("fare_amount"),
                        col("tip_amount"),
                        col("total_amount")
                    ))

        logger.info(f"After filtering: {df_clean.count()} records")

        # Create text representation for embedding
        logger.info("Creating text representations...")
        df_with_text = create_text_representation(df_clean)

        # Generate embeddings via gRPC
        logger.info("Generating embeddings (this may take a while)...")
        embedding_udf = create_embedding_udf()
        df_with_embeddings = df_with_text.withColumn(
            "embedding",
            embedding_udf(col("text_repr"))
        )

        # Add metadata columns + year/month partition keys extracted from pickup datetime.
        # Partitioning by pickup_year/pickup_month enables partition pruning when
        # queries are scoped to a time range (e.g., "find similar rides from 2023").
        df_final = (df_with_embeddings
                    .withColumn("ingestion_timestamp", lit(datetime.utcnow()))
                    .withColumn("model_name", lit("all-MiniLM-L6-v2"))
                    .withColumn("embedding_dimension", lit(384))
                    .withColumn("pickup_year", year(col("tpep_pickup_datetime")))
                    .withColumn("pickup_month", month(col("tpep_pickup_datetime"))))

        # Write to Delta Lake partitioned by year/month for efficient time-scoped queries.
        logger.info(f"Writing to Delta Lake: {DELTA_OUTPUT_PATH}")
        (df_final
         .write
         .format("delta")
         .mode("overwrite")  # Change to "append" for incremental loads
         .partitionBy("pickup_year", "pickup_month")
         .save(DELTA_OUTPUT_PATH))

        logger.info("✓ Ingestion job completed successfully!")

        # Show sample results
        logger.info("Sample embedded records:")
        df_final.select("PULocationID", "DOLocationID", "trip_distance", "fare_amount", "embedding_dimension").show(5, truncate=False)

    except Exception as e:
        logger.error(f"Job failed with error: {e}", exc_info=True)
        sys.exit(1)
    finally:
        spark.stop()


if __name__ == "__main__":
    start_time = time.time()
    main()
    elapsed = time.time() - start_time
    logger.info(f"Total execution time: {elapsed:.2f} seconds")
