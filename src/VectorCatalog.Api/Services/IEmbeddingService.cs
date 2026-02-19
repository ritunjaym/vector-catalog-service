namespace VectorCatalog.Api.Services;

public interface IEmbeddingService
{
    /// <summary>
    /// Generates a vector embedding for the given text using the Python sidecar.
    /// </summary>
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);
}
