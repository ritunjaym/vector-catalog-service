"""
gRPC Server Entry Point
Starts both EmbeddingService and IndexService on port 50051.
Supports graceful shutdown via SIGTERM/SIGINT.
"""
import logging
import os
import signal
import sys
from concurrent import futures
import grpc
from embedding_service import EmbeddingServiceImpl
from index_service import IndexServiceImpl
import vector_service_pb2_grpc

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s',
    handlers=[
        logging.StreamHandler(sys.stdout)
    ]
)
logger = logging.getLogger(__name__)

# Configuration from environment variables
GRPC_PORT = int(os.getenv('GRPC_PORT', '50051'))
EMBEDDING_MODEL = os.getenv('EMBEDDING_MODEL', 'all-MiniLM-L6-v2')
INDEX_DIR = os.getenv('INDEX_DIR', '/data/indexes')
MAX_WORKERS = int(os.getenv('MAX_WORKERS', '10'))


def create_server():
    """
    Create and configure the gRPC server with both services.

    Returns:
        Configured gRPC server instance
    """
    # Create thread pool for handling concurrent requests
    server = grpc.server(
        futures.ThreadPoolExecutor(max_workers=MAX_WORKERS),
        options=[
            ('grpc.max_send_message_length', 100 * 1024 * 1024),  # 100 MB
            ('grpc.max_receive_message_length', 100 * 1024 * 1024),  # 100 MB
            ('grpc.keepalive_time_ms', 30000),  # 30 seconds
            ('grpc.keepalive_timeout_ms', 10000),  # 10 seconds
            ('grpc.keepalive_permit_without_calls', True),
            ('grpc.http2.max_pings_without_data', 0),
        ]
    )

    # Initialize and register services
    logger.info(f"Initializing EmbeddingService with model: {EMBEDDING_MODEL}")
    embedding_service = EmbeddingServiceImpl(model_name=EMBEDDING_MODEL)
    vector_service_pb2_grpc.add_EmbeddingServiceServicer_to_server(embedding_service, server)

    logger.info(f"Initializing IndexService with index directory: {INDEX_DIR}")
    index_service = IndexServiceImpl(index_dir=INDEX_DIR)
    vector_service_pb2_grpc.add_IndexServiceServicer_to_server(index_service, server)

    # Bind to all interfaces on specified port
    server.add_insecure_port(f'[::]:{GRPC_PORT}')

    return server


def serve():
    """
    Start the gRPC server and handle graceful shutdown.
    """
    server = create_server()

    # Graceful shutdown handler
    def signal_handler(sig, frame):
        logger.info("Received shutdown signal, stopping server...")
        server.stop(grace=5)  # 5 second grace period
        sys.exit(0)

    signal.signal(signal.SIGINT, signal_handler)
    signal.signal(signal.SIGTERM, signal_handler)

    logger.info(f"Starting gRPC server on port {GRPC_PORT}")
    logger.info(f"Configuration:")
    logger.info(f"  - Embedding Model: {EMBEDDING_MODEL}")
    logger.info(f"  - Index Directory: {INDEX_DIR}")
    logger.info(f"  - Max Workers: {MAX_WORKERS}")

    server.start()
    logger.info(f"gRPC server is listening on port {GRPC_PORT}")

    try:
        server.wait_for_termination()
    except KeyboardInterrupt:
        logger.info("Server interrupted by user")
        server.stop(grace=5)


if __name__ == '__main__':
    logger.info("=" * 60)
    logger.info("Vector Catalog Service - gRPC Sidecar")
    logger.info("=" * 60)
    serve()
