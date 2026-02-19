using Grpc.Core;
using Polly.CircuitBreaker;
using VectorCatalog.Api.Protos;
using VectorCatalog.Api.Services;

namespace VectorCatalog.Api.Infrastructure.Resilience;

/// <summary>
/// Decorator around IEmbeddingService that applies Polly resilience policies.
/// Uses the decorator pattern so resilience is layered without polluting service logic.
/// </summary>
public class ResilientEmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingService _inner;
    private readonly ILogger<ResilientEmbeddingService> _logger;

    // Shared circuit breaker instance — must be singleton to track state across requests
    private static readonly Polly.IAsyncPolicy<float[]> _policy =
        ResiliencePolicies.GetCombinedGrpcPolicy<float[]>(
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            "GenerateEmbedding",
            timeoutSeconds: 10);

    public ResilientEmbeddingService(
        IEmbeddingService inner,
        ILogger<ResilientEmbeddingService> logger)
    {
        _inner = inner;
        _logger = logger;
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _policy.ExecuteAsync(
                ct => _inner.GenerateEmbeddingAsync(text, ct),
                cancellationToken);
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError("Embedding circuit breaker is open — sidecar unavailable");
            throw new InvalidOperationException("Embedding service is temporarily unavailable. Try again later.");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            _logger.LogError("Embedding sidecar permanently unavailable after retries");
            throw;
        }
    }
}
