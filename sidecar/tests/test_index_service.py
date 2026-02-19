"""
Unit tests for IndexService
Tests FAISS index loading, search, and reload functionality.
"""
import sys
import os
import pytest
import tempfile
import shutil
import numpy as np
import faiss

# Add parent directory to path
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from index_service import IndexServiceImpl, ShardIndex
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


@pytest.fixture
def temp_index_dir():
    """Create a temporary directory with a test FAISS index"""
    temp_dir = tempfile.mkdtemp()

    # Create a simple flat L2 index for testing
    dimension = 384
    n_vectors = 1000

    # Generate random vectors
    np.random.seed(42)
    vectors = np.random.random((n_vectors, dimension)).astype('float32')

    # Create and train index
    index = faiss.IndexFlatL2(dimension)
    index.add(vectors)

    # Save to temp directory
    index_path = os.path.join(temp_dir, "test_shard.index")
    faiss.write_index(index, index_path)

    yield temp_dir

    # Cleanup
    shutil.rmtree(temp_dir)


def test_index_service_init_empty_dir():
    """Test IndexService handles missing index directory gracefully"""
    with tempfile.TemporaryDirectory() as temp_dir:
        non_existent_dir = os.path.join(temp_dir, "nonexistent")
        service = IndexServiceImpl(index_dir=non_existent_dir)
        assert len(service.shards) == 0


def test_index_service_loads_shards(temp_index_dir):
    """Test IndexService loads index files on startup"""
    service = IndexServiceImpl(index_dir=temp_index_dir)
    assert len(service.shards) == 1
    assert "test_shard" in service.shards


def test_shard_index_properties(temp_index_dir):
    """Test ShardIndex correctly loads index properties"""
    index_path = os.path.join(temp_index_dir, "test_shard.index")
    shard = ShardIndex(shard_key="test_shard", index_path=index_path)

    assert shard.shard_key == "test_shard"
    assert shard.dimension == 384
    assert shard.total_vectors == 1000


def test_shard_search(temp_index_dir):
    """Test ShardIndex search functionality"""
    index_path = os.path.join(temp_index_dir, "test_shard.index")
    shard = ShardIndex(shard_key="test_shard", index_path=index_path)

    # Create a random query vector
    query = np.random.random(384).astype('float32')

    distances, indices = shard.search(query, top_k=5, nprobe=1)

    assert len(distances) == 5
    assert len(indices) == 5
    assert all(isinstance(d, (float, np.floating)) for d in distances)
    assert all(isinstance(i, (int, np.integer)) for i in indices)
    assert all(0 <= i < 1000 for i in indices)


def test_index_service_search(temp_index_dir):
    """Test IndexService SearchIndex RPC"""
    service = IndexServiceImpl(index_dir=temp_index_dir)

    query_vector = np.random.random(384).astype('float32').tolist()
    request = vector_service_pb2.SearchRequest(
        shard_key="test_shard",
        query_vector=query_vector,
        top_k=10,
        nprobe=5
    )
    context = MockContext()

    response = service.SearchIndex(request, context)

    assert len(response.results) == 10
    assert response.shard_key == "test_shard"
    assert context.code is None  # No error

    # Check that results have proper structure
    for result in response.results:
        assert hasattr(result, 'id')
        assert hasattr(result, 'score')
        assert 0 <= result.id < 1000  # Within our test data range


def test_index_service_search_wrong_shard(temp_index_dir):
    """Test IndexService returns error for non-existent shard"""
    service = IndexServiceImpl(index_dir=temp_index_dir)

    query_vector = np.random.random(384).astype('float32').tolist()
    request = vector_service_pb2.SearchRequest(
        shard_key="nonexistent_shard",
        query_vector=query_vector,
        top_k=10,
        nprobe=5
    )
    context = MockContext()

    response = service.SearchIndex(request, context)

    assert context.code is not None  # Should set error code
    assert "not found" in context.details.lower()


def test_index_service_search_wrong_dimension(temp_index_dir):
    """Test IndexService rejects query with wrong dimension"""
    service = IndexServiceImpl(index_dir=temp_index_dir)

    # Wrong dimension (128 instead of 384)
    query_vector = np.random.random(128).astype('float32').tolist()
    request = vector_service_pb2.SearchRequest(
        shard_key="test_shard",
        query_vector=query_vector,
        top_k=10,
        nprobe=5
    )
    context = MockContext()

    response = service.SearchIndex(request, context)

    assert context.code is not None
    assert "dimension" in context.details.lower()


def test_index_service_get_info(temp_index_dir):
    """Test IndexService GetIndexInfo RPC"""
    service = IndexServiceImpl(index_dir=temp_index_dir)

    # GetIndexInfo takes IndexInfoRequest
    request = vector_service_pb2.IndexInfoRequest(shard_key="")
    context = MockContext()

    response = service.GetIndexInfo(request, context)

    assert len(response.shards) == 1
    shard_info = response.shards[0]
    assert shard_info.shard_key == "test_shard"
    assert shard_info.total_vectors == 1000
    assert shard_info.dimension == 384


def test_index_service_reload(temp_index_dir):
    """Test IndexService ReloadIndex RPC"""
    service = IndexServiceImpl(index_dir=temp_index_dir)

    request = vector_service_pb2.ReloadIndexRequest(shard_key="test_shard")
    context = MockContext()

    response = service.ReloadIndex(request, context)

    assert response.success is True
    assert "success" in response.message.lower()
    assert "test_shard" in response.reloaded_shards


def test_index_service_reload_nonexistent(temp_index_dir):
    """Test IndexService reload fails for non-existent shard"""
    service = IndexServiceImpl(index_dir=temp_index_dir)

    request = vector_service_pb2.ReloadIndexRequest(shard_key="nonexistent")
    context = MockContext()

    response = service.ReloadIndex(request, context)

    assert response.success is False
    assert context.code is not None
    assert len(response.reloaded_shards) == 0
