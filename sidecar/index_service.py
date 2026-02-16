"""
Index Service Implementation
Manages FAISS IVF-PQ indexes for approximate nearest neighbor search.
Supports multi-shard indexing, hot reloading, and configurable search parameters.
"""
import logging
import os
from typing import Dict, Optional
import grpc
import faiss
import numpy as np
import vector_service_pb2
import vector_service_pb2_grpc

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)


class ShardIndex:
    """
    Represents a single FAISS index shard.
    Encapsulates index metadata and provides search functionality.
    """

    def __init__(self, shard_key: str, index_path: str):
        """
        Load a FAISS index from disk.

        Args:
            shard_key: Unique identifier for this shard (e.g., "nyc_taxi_2023")
            index_path: Filesystem path to the .index file
        """
        self.shard_key = shard_key
        self.index_path = index_path

        if not os.path.exists(index_path):
            raise FileNotFoundError(f"Index file not found: {index_path}")

        logger.info(f"Loading FAISS index for shard '{shard_key}' from {index_path}")
        self.index = faiss.read_index(index_path)
        self.dimension = self.index.d
        self.total_vectors = self.index.ntotal

        logger.info(f"Shard '{shard_key}' loaded: {self.total_vectors} vectors, dimension={self.dimension}")

    def search(self, query_vector: np.ndarray, top_k: int, nprobe: int) -> tuple:
        """
        Perform approximate nearest neighbor search.

        Args:
            query_vector: Query embedding (numpy array of shape [dimension])
            top_k: Number of nearest neighbors to return
            nprobe: Number of IVF cells to probe (higher = more accurate but slower)

        Returns:
            Tuple of (distances, indices) - both numpy arrays of shape [top_k]
        """
        # Ensure query is 2D for FAISS (shape: [1, dimension])
        if query_vector.ndim == 1:
            query_vector = query_vector.reshape(1, -1)

        # Set nprobe parameter for IVF index
        if hasattr(self.index, 'nprobe'):
            self.index.nprobe = nprobe

        # FAISS search returns (distances, indices)
        distances, indices = self.index.search(query_vector, top_k)

        # Return flattened results (remove batch dimension)
        return distances[0], indices[0]

    def reload(self) -> bool:
        """
        Hot reload the index from disk without downtime.

        Returns:
            True if reload succeeded, False otherwise
        """
        try:
            logger.info(f"Reloading index for shard '{self.shard_key}'")
            new_index = faiss.read_index(self.index_path)
            self.index = new_index
            self.dimension = self.index.d
            self.total_vectors = self.index.ntotal
            logger.info(f"Shard '{self.shard_key}' reloaded: {self.total_vectors} vectors")
            return True
        except Exception as e:
            logger.error(f"Failed to reload shard '{self.shard_key}': {e}")
            return False


class IndexServiceImpl(vector_service_pb2_grpc.IndexServiceServicer):
    """
    Implements the IndexService gRPC service.
    Manages multiple FAISS index shards and handles search requests.
    """

    def __init__(self, index_dir: str = "/data/indexes"):
        """
        Initialize the index service.

        Args:
            index_dir: Directory containing .index files (one per shard)
        """
        self.index_dir = index_dir
        self.shards: Dict[str, ShardIndex] = {}
        self._load_all_shards()

    def _load_all_shards(self):
        """
        Discover and load all .index files in the index directory.
        Each filename (without extension) becomes a shard key.
        """
        if not os.path.exists(self.index_dir):
            logger.warning(f"Index directory does not exist: {self.index_dir}. Creating it.")
            os.makedirs(self.index_dir, exist_ok=True)
            return

        index_files = [f for f in os.listdir(self.index_dir) if f.endswith('.index')]

        if not index_files:
            logger.warning(f"No .index files found in {self.index_dir}")
            return

        for filename in index_files:
            shard_key = filename.replace('.index', '')
            index_path = os.path.join(self.index_dir, filename)
            try:
                self.shards[shard_key] = ShardIndex(shard_key, index_path)
            except Exception as e:
                logger.error(f"Failed to load shard '{shard_key}': {e}")

        logger.info(f"Loaded {len(self.shards)} shard(s): {list(self.shards.keys())}")

    def SearchIndex(self, request, context):
        """
        Search a FAISS index shard with a query vector.

        Args:
            request: SearchRequest with shard_key, query_vector, top_k, nprobe
            context: gRPC context

        Returns:
            SearchResponse with distances and indices
        """
        try:
            shard_key = request.shard_key or "nyc_taxi_2023"  # Default shard

            if shard_key not in self.shards:
                context.set_code(grpc.StatusCode.NOT_FOUND)
                context.set_details(f"Shard '{shard_key}' not found. Available shards: {list(self.shards.keys())}")
                return vector_service_pb2.SearchResponse()

            shard = self.shards[shard_key]

            # Convert repeated float field to numpy array
            query_vector = np.array(request.query_vector, dtype=np.float32)

            if query_vector.shape[0] != shard.dimension:
                context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
                context.set_details(f"Query dimension {query_vector.shape[0]} does not match index dimension {shard.dimension}")
                return vector_service_pb2.SearchResponse()

            top_k = request.top_k if request.top_k > 0 else 10
            nprobe = request.nprobe if request.nprobe > 0 else 10

            logger.debug(f"Searching shard '{shard_key}' with top_k={top_k}, nprobe={nprobe}")

            distances, indices = shard.search(query_vector, top_k, nprobe)

            # Convert numpy results to SearchResult messages
            results = []
            for idx, (distance, doc_id) in enumerate(zip(distances, indices)):
                result = vector_service_pb2.SearchResult(
                    id=int(doc_id),
                    score=float(distance),
                    metadata_json=""  # No metadata in this simple implementation
                )
                results.append(result)

            return vector_service_pb2.SearchResponse(
                results=results,
                shard_key=shard_key,
                search_latency_ms=0.0,  # Could add timing here
                cache_hit=False
            )

        except Exception as e:
            logger.error(f"Error during search: {str(e)}", exc_info=True)
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(f"Search failed: {str(e)}")
            return vector_service_pb2.SearchResponse()

    def GetIndexInfo(self, request, context):
        """
        Get metadata about all loaded index shards.

        Args:
            request: IndexInfoRequest (can filter by shard_key)
            context: gRPC context

        Returns:
            IndexInfoResponse with list of ShardInfo
        """
        try:
            shard_infos = []
            for shard_key, shard in self.shards.items():
                shard_info = vector_service_pb2.ShardInfo(
                    shard_key=shard_key,
                    total_vectors=shard.total_vectors,
                    dimension=shard.dimension,
                    index_path=shard.index_path
                )
                shard_infos.append(shard_info)

            logger.debug(f"Returning info for {len(shard_infos)} shards")

            return vector_service_pb2.IndexInfoResponse(shards=shard_infos)

        except Exception as e:
            logger.error(f"Error getting index info: {str(e)}", exc_info=True)
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(f"Failed to get index info: {str(e)}")
            return vector_service_pb2.IndexInfoResponse()

    def ReloadIndex(self, request, context):
        """
        Hot reload a specific index shard from disk.

        Args:
            request: ReloadIndexRequest with shard_key
            context: gRPC context

        Returns:
            ReloadIndexResponse with success status
        """
        try:
            shard_key = request.shard_key

            if not shard_key:
                context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
                context.set_details("shard_key is required")
                return vector_service_pb2.ReloadIndexResponse(
                    success=False,
                    message="shard_key is required",
                    reloaded_shards=[]
                )

            if shard_key not in self.shards:
                context.set_code(grpc.StatusCode.NOT_FOUND)
                context.set_details(f"Shard '{shard_key}' not found")
                return vector_service_pb2.ReloadIndexResponse(
                    success=False,
                    message=f"Shard '{shard_key}' not found. Available shards: {list(self.shards.keys())}",
                    reloaded_shards=[]
                )

            success = self.shards[shard_key].reload()

            if success:
                return vector_service_pb2.ReloadIndexResponse(
                    success=True,
                    message=f"Shard '{shard_key}' reloaded successfully",
                    reloaded_shards=[shard_key]
                )
            else:
                return vector_service_pb2.ReloadIndexResponse(
                    success=False,
                    message=f"Failed to reload shard '{shard_key}'",
                    reloaded_shards=[]
                )

        except Exception as e:
            logger.error(f"Error reloading index: {str(e)}", exc_info=True)
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(f"Reload failed: {str(e)}")
            return vector_service_pb2.ReloadIndexResponse(success=False, message=str(e), reloaded_shards=[])
