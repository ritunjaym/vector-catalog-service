"""
Unit tests for EmbeddingService
Tests embedding generation for single and batch requests.
"""
import sys
import os
import pytest
import numpy as np

# Add parent directory to path so we can import sidecar modules
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from embedding_service import EmbeddingServiceImpl
import vector_service_pb2


class MockContext:
    """Mock gRPC context for testing"""

    def __init__(self):
        self.code = None
        self.details = None

    def set_code(self, code):
        self.code = code

    def set_details(self, details):
        self.details = details


@pytest.fixture(scope="module")
def embedding_service():
    """
    Create EmbeddingServiceImpl instance once per module.
    Model loading is expensive, so we reuse it across tests.
    """
    return EmbeddingServiceImpl(model_name="all-MiniLM-L6-v2")


def test_generate_embedding_single(embedding_service):
    """Test single embedding generation"""
    request = vector_service_pb2.EmbeddingRequest(
        text="Hello world",
        model_name="all-MiniLM-L6-v2"
    )
    context = MockContext()

    response = embedding_service.GenerateEmbedding(request, context)

    assert response.dimension == 384  # all-MiniLM-L6-v2 has 384 dimensions
    assert len(response.vector) == 384
    assert all(isinstance(v, float) for v in response.vector)
    assert response.model_name == "all-MiniLM-L6-v2"


def test_generate_embedding_empty_text(embedding_service):
    """Test that empty text is rejected"""
    request = vector_service_pb2.EmbeddingRequest(text="", model_name="all-MiniLM-L6-v2")
    context = MockContext()

    response = embedding_service.GenerateEmbedding(request, context)

    assert context.code is not None  # Should set error code
    assert "empty" in context.details.lower()


def test_generate_embedding_consistency(embedding_service):
    """Test that same input produces same embedding"""
    text = "The quick brown fox jumps over the lazy dog"
    request = vector_service_pb2.EmbeddingRequest(text=text, model_name="all-MiniLM-L6-v2")
    context = MockContext()

    response1 = embedding_service.GenerateEmbedding(request, context)
    response2 = embedding_service.GenerateEmbedding(request, context)

    # Convert to numpy for easy comparison
    vec1 = np.array(response1.vector)
    vec2 = np.array(response2.vector)

    # Should be identical (deterministic)
    np.testing.assert_array_almost_equal(vec1, vec2, decimal=6)


def test_generate_embedding_batch(embedding_service):
    """Test batch embedding generation"""
    texts = [
        "First document",
        "Second document",
        "Third document with more words"
    ]
    request = vector_service_pb2.EmbeddingBatchRequest(texts=texts)
    context = MockContext()

    response = embedding_service.GenerateEmbeddingBatch(request, context)

    assert len(response.embeddings) == 3
    for emb in response.embeddings:
        assert emb.dimension == 384
        assert len(emb.vector) == 384
        assert emb.model_name == "all-MiniLM-L6-v2"


def test_generate_embedding_batch_empty(embedding_service):
    """Test that empty batch is rejected"""
    request = vector_service_pb2.EmbeddingBatchRequest(texts=[])
    context = MockContext()

    response = embedding_service.GenerateEmbeddingBatch(request, context)

    assert context.code is not None
    assert "required" in context.details.lower() or "empty" in context.details.lower()


def test_embedding_similarity(embedding_service):
    """Test that similar texts have similar embeddings"""
    request1 = vector_service_pb2.EmbeddingRequest(text="I love cats", model_name="all-MiniLM-L6-v2")
    request2 = vector_service_pb2.EmbeddingRequest(text="I adore felines", model_name="all-MiniLM-L6-v2")
    request3 = vector_service_pb2.EmbeddingRequest(text="Quantum physics is complex", model_name="all-MiniLM-L6-v2")
    context = MockContext()

    resp1 = embedding_service.GenerateEmbedding(request1, context)
    resp2 = embedding_service.GenerateEmbedding(request2, context)
    resp3 = embedding_service.GenerateEmbedding(request3, context)

    vec1 = np.array(resp1.vector)
    vec2 = np.array(resp2.vector)
    vec3 = np.array(resp3.vector)

    # Cosine similarity
    def cosine_sim(a, b):
        return np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b))

    sim_12 = cosine_sim(vec1, vec2)
    sim_13 = cosine_sim(vec1, vec3)

    # "cats" and "felines" should be more similar than "cats" and "quantum physics"
    assert sim_12 > sim_13
