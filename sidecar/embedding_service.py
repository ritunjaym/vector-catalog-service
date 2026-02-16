"""
Embedding Service Implementation
Generates semantic embeddings using sentence-transformers (all-MiniLM-L6-v2).
Supports single and batch embedding generation.
"""
import logging
from typing import List
import grpc
import numpy as np
from sentence_transformers import SentenceTransformer
import vector_service_pb2
import vector_service_pb2_grpc

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)


class EmbeddingServiceImpl(vector_service_pb2_grpc.EmbeddingServiceServicer):
    """
    Implements the EmbeddingService gRPC service.
    Loads sentence-transformers model on initialization and caches it in memory.
    """

    def __init__(self, model_name: str = "all-MiniLM-L6-v2"):
        """
        Initialize the embedding service with a sentence-transformers model.

        Args:
            model_name: HuggingFace model identifier (default: all-MiniLM-L6-v2)
        """
        logger.info(f"Loading embedding model: {model_name}")
        self.model = SentenceTransformer(model_name)
        self.model_name = model_name
        logger.info(f"Model {model_name} loaded successfully. Embedding dimension: {self.model.get_sentence_embedding_dimension()}")

    def GenerateEmbedding(self, request, context):
        """
        Generate embedding for a single text input.

        Args:
            request: EmbeddingRequest with text and optional model_name
            context: gRPC context

        Returns:
            EmbeddingResponse with float vector
        """
        try:
            text = request.text.strip()
            if not text:
                context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
                context.set_details("Text cannot be empty")
                return vector_service_pb2.EmbeddingResponse()

            logger.debug(f"Generating embedding for text: '{text[:50]}...'")

            # Generate embedding (returns numpy array)
            embedding = self.model.encode(text, convert_to_numpy=True)

            # Convert to list of floats for protobuf
            vector = embedding.tolist()

            logger.debug(f"Generated embedding with dimension: {len(vector)}")

            return vector_service_pb2.EmbeddingResponse(
                vector=vector,
                model_name=self.model_name,
                dimension=len(vector)
            )

        except Exception as e:
            logger.error(f"Error generating embedding: {str(e)}", exc_info=True)
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(f"Failed to generate embedding: {str(e)}")
            return vector_service_pb2.EmbeddingResponse()

    def GenerateEmbeddingBatch(self, request, context):
        """
        Generate embeddings for multiple texts in a single batch.
        More efficient than multiple single requests due to batched inference.

        Args:
            request: EmbeddingBatchRequest with list of texts
            context: gRPC context

        Returns:
            EmbeddingBatchResponse with list of embeddings
        """
        try:
            texts = [t.strip() for t in request.texts if t.strip()]

            if not texts:
                context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
                context.set_details("At least one non-empty text is required")
                return vector_service_pb2.EmbeddingBatchResponse()

            logger.info(f"Generating batch embeddings for {len(texts)} texts")

            # Batch encode for efficiency
            embeddings = self.model.encode(texts, convert_to_numpy=True, show_progress_bar=False)

            # Convert numpy arrays to EmbeddingResponse messages
            responses = []
            for embedding in embeddings:
                vector = embedding.tolist()
                responses.append(
                    vector_service_pb2.EmbeddingResponse(
                        vector=vector,
                        model_name=self.model_name,
                        dimension=len(vector)
                    )
                )

            logger.info(f"Successfully generated {len(responses)} embeddings")

            return vector_service_pb2.EmbeddingBatchResponse(embeddings=responses)

        except Exception as e:
            logger.error(f"Error generating batch embeddings: {str(e)}", exc_info=True)
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(f"Failed to generate batch embeddings: {str(e)}")
            return vector_service_pb2.EmbeddingBatchResponse()
