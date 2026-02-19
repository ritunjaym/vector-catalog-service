using Grpc.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using VectorCatalog.Api.Configuration;
using VectorCatalog.Api.Protos;

namespace VectorCatalog.Api.Infrastructure.Health;

public class SidecarHealthCheck : IHealthCheck
{
    private readonly IndexService.IndexServiceClient _indexClient;
    private readonly ILogger<SidecarHealthCheck> _logger;

    public SidecarHealthCheck(IndexService.IndexServiceClient indexClient, ILogger<SidecarHealthCheck> logger)
    {
        _indexClient = indexClient;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            var response = await _indexClient.GetIndexInfoAsync(
                new IndexInfoRequest { ShardKey = "" },
                deadline: deadline,
                cancellationToken: cancellationToken);

            return HealthCheckResult.Healthy(
                $"Sidecar healthy. Shards loaded: {response.Shards.Count}");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            _logger.LogWarning("Sidecar unavailable during health check: {Message}", ex.Message);
            return HealthCheckResult.Unhealthy("Sidecar gRPC service is unavailable", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sidecar health check failed unexpectedly");
            return HealthCheckResult.Degraded("Sidecar health check encountered an error", ex);
        }
    }
}
