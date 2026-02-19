# ══════════════════════════════════════════════════════════════════════════════
# Multi-Stage Dockerfile for Python gRPC Sidecar
# Pre-downloads sentence-transformers model for faster startup
# ══════════════════════════════════════════════════════════════════════════════

# ── Stage 1: Build and download model ─────────────────────────────────────────
FROM python:3.12-slim AS builder

WORKDIR /build

# Install system dependencies for building Python packages
RUN apt-get update && apt-get install -y \
    build-essential \
    && rm -rf /var/lib/apt/lists/*

# Copy requirements and install dependencies
COPY requirements.txt .
RUN pip install --no-cache-dir --user -r requirements.txt

# Pre-download the sentence-transformers model (saves ~5s on startup)
RUN python3 -c "from sentence_transformers import SentenceTransformer; SentenceTransformer('all-MiniLM-L6-v2')"

# ── Stage 2: Final runtime image ──────────────────────────────────────────────
FROM python:3.12-slim

# Install runtime dependencies
RUN apt-get update && apt-get install -y \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user
RUN groupadd -r sidecar && useradd -r -g sidecar sidecar

WORKDIR /app

# Copy Python packages from builder
COPY --from=builder /root/.local /home/sidecar/.local

# Copy sentence-transformers model cache from builder
COPY --from=builder /root/.cache /home/sidecar/.cache

# Copy application code
COPY embedding_service.py .
COPY index_service.py .
COPY server.py .
COPY vector_service_pb2.py .
COPY vector_service_pb2_grpc.py .

# Set ownership
RUN chown -R sidecar:sidecar /app /home/sidecar

# Switch to non-root user
USER sidecar

# Add local Python packages to PATH
ENV PATH="/home/sidecar/.local/bin:${PATH}" \
    PYTHONPATH="/app:${PYTHONPATH}" \
    GRPC_PORT=50051 \
    INDEX_DIR=/data/indexes \
    EMBEDDING_MODEL=all-MiniLM-L6-v2 \
    MAX_WORKERS=10

# Expose gRPC port
EXPOSE 50051

# Health check (uses grpc-health-probe in production, simple curl for dev)
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD python3 -c "import grpc; import vector_service_pb2_grpc; channel = grpc.insecure_channel('localhost:50051'); stub = vector_service_pb2_grpc.IndexServiceStub(channel); stub.GetIndexInfo(None, timeout=3)" || exit 1

ENTRYPOINT ["python3", "server.py"]
