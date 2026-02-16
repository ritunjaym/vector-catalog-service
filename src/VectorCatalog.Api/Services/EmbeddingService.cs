using System.Diagnostics;
using Microsoft.Extensions.Options;
using VectorCatalog.Api.Configuration;
using VectorCatalog.Api.Infrastructure.Observability;
using VectorCatalog.Api.Protos;

namespace VectorCatalog.Api.Services;

public class EmbeddingService : IEmbeddingService
{
    private readonly Protos.EmbeddingService.EmbeddingServiceClient _grpcClient;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(
        Protos.EmbeddingService.EmbeddingServiceClient grpcClient,
        ILogger<EmbeddingService> logger)
    {
        _grpcClient = grpcClient;
        _logger = logger;
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Generating embedding for text (length={Length})", text.Length);

        // Enrich OpenTelemetry activity with embedding request details
        ActivityEnricher.EnrichEmbeddingActivity(Activity.Current, text);

        var request = new EmbeddingRequest
        {
            Text = text,
            ModelName = "all-MiniLM-L6-v2"
        };

        var response = await _grpcClient.GenerateEmbeddingAsync(request, cancellationToken: cancellationToken);

        _logger.LogDebug("Embedding generated: dim={Dimension}, latency={Latency}ms",
            response.Dimension, response.LatencyMs);

        var vector = response.Vector.ToArray();

        // Enrich OpenTelemetry activity with embedding response details
        ActivityEnricher.EnrichEmbeddingActivity(Activity.Current, text, vector);

        return vector;
    }
}
