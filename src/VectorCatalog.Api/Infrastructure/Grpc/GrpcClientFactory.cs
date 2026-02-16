using Grpc.Net.Client;
using Microsoft.Extensions.Options;
using VectorCatalog.Api.Configuration;
using VectorCatalog.Api.Protos;

namespace VectorCatalog.Api.Infrastructure.Grpc;

public static class GrpcClientExtensions
{
    public static IServiceCollection AddSidecarGrpcClients(this IServiceCollection services)
    {
        services.AddGrpcClient<EmbeddingService.EmbeddingServiceClient>((sp, options) =>
        {
            var config = sp.GetRequiredService<IOptions<VectorCatalogOptions>>().Value;
            options.Address = new Uri(config.SidecarGrpcAddress);
        })
        .ConfigureChannel(options =>
        {
            options.HttpHandler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                EnableMultipleHttp2Connections = true
            };
        });

        services.AddGrpcClient<IndexService.IndexServiceClient>((sp, options) =>
        {
            var config = sp.GetRequiredService<IOptions<VectorCatalogOptions>>().Value;
            options.Address = new Uri(config.SidecarGrpcAddress);
        });

        return services;
    }
}
