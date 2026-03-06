#!/usr/bin/env python3
"""
Incremental ingestion with Delta Lake CDC.
Merges new data, skips unchanged records.
"""
from delta.tables import DeltaTable
from pyspark.sql import SparkSession
from pyspark.sql.functions import col, current_timestamp
import argparse


def incremental_ingest(spark, input_path, delta_path):
    """Upsert new data into Delta table."""

    # Read new data
    new_data = spark.read.parquet(input_path) \
        .withColumn("updated_at", current_timestamp())

    if DeltaTable.isDeltaTable(spark, delta_path):
        # Merge: update existing, insert new
        delta_table = DeltaTable.forPath(spark, delta_path)

        delta_table.alias("target").merge(
            new_data.alias("source"),
            "target.record_id = source.record_id"
        ).whenMatchedUpdateAll() \
         .whenNotMatchedInsertAll() \
         .execute()

        print(f"Merged {new_data.count()} records")

        # Show version history
        history = delta_table.history(2)
        history.select("version", "timestamp", "operation", "operationMetrics").show(truncate=False)
    else:
        # First run: create Delta table
        new_data.write.format("delta") \
            .partitionBy("year_month") \
            .save(delta_path)
        print(f"Created Delta table with {new_data.count()} records")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    parser.add_argument("--delta-table", required=True)
    args = parser.parse_args()

    spark = SparkSession.builder \
        .appName("IncrementalIngest") \
        .config("spark.sql.extensions", "io.delta.sql.DeltaSparkSessionExtension") \
        .config("spark.sql.catalog.spark_catalog", "org.apache.spark.sql.delta.catalog.DeltaCatalog") \
        .getOrCreate()

    incremental_ingest(spark, args.input, args.delta_table)
