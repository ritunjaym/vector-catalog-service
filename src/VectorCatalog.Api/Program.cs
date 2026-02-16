using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using VectorCatalog.Api.Configuration;
using VectorCatalog.Api.Infrastructure.Grpc;
using VectorCatalog.Api.Infrastructure.Health;
using VectorCatalog.Api.Infrastructure.Observability;
using VectorCatalog.Api.Infrastructure.Resilience;
using VectorCatalog.Api.Models;
using VectorCatalog.Api.Services;

// ── Bootstrap Serilog early so startup errors are captured ───────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting vector-catalog-api");

    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog ───────────────────────────────────────────────────────────────
    builder.Host.UseSerilog((ctx, services, config) =>
        config.ReadFrom.Configuration(ctx.Configuration)
              .ReadFrom.Services(services)
              .Enrich.FromLogContext());

    // ── Configuration ─────────────────────────────────────────────────────────
    builder.Services.Configure<VectorCatalogOptions>(
        builder.Configuration.GetSection(VectorCatalogOptions.SectionName));

    // ── Redis ─────────────────────────────────────────────────────────────────
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = builder.Configuration
            .GetSection($"{VectorCatalogOptions.SectionName}:Redis:ConnectionString")
            .Value ?? "redis:6379";
        options.InstanceName = "vc:";
    });

    // ── gRPC Clients ──────────────────────────────────────────────────────────
    builder.Services.AddSidecarGrpcClients();

    // ── Application Services ──────────────────────────────────────────────────
    builder.Services.AddScoped<EmbeddingService>();          // concrete impl
    builder.Services.AddScoped<IEmbeddingService>(sp =>      // resilience decorator
        new ResilientEmbeddingService(
            sp.GetRequiredService<EmbeddingService>(),
            sp.GetRequiredService<ILogger<ResilientEmbeddingService>>()));
    builder.Services.AddScoped<ISearchService, SearchService>();
    builder.Services.AddScoped<ResilientIndexService>();
    builder.Services.AddSingleton<ICacheService, RedisCacheService>();
    builder.Services.AddSingleton<VectorCatalogMetrics>();
    builder.Services.AddSingleton<ServiceInfo>(_ => new ServiceInfo
    {
        Name = "vector-catalog-api",
        Version = "0.2.0",
        Environment = builder.Environment.EnvironmentName,
        StartedAt = DateTime.UtcNow
    });

    // ── Health Checks ─────────────────────────────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddRedis(
            builder.Configuration.GetSection($"{VectorCatalogOptions.SectionName}:Redis:ConnectionString").Value
                ?? "redis:6379",
            name: "redis",
            tags: ["ready"])
        .AddCheck<SidecarHealthCheck>("sidecar-grpc", tags: ["ready"]);

    // ── OpenTelemetry ─────────────────────────────────────────────────────────
    var otelServiceName = builder.Configuration["OpenTelemetry:ServiceName"] ?? "vector-catalog-api";
    var otelServiceVersion = builder.Configuration["OpenTelemetry:ServiceVersion"] ?? "0.1.0";

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r
            .AddService(serviceName: otelServiceName, serviceVersion: otelServiceVersion))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddGrpcClientInstrumentation()
            .AddJaegerExporter(opts =>
            {
                opts.Endpoint = new Uri(
                    builder.Configuration["OpenTelemetry:JaegerEndpoint"]
                        ?? "http://jaeger:14268/api/traces");
            }))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddMeter(VectorCatalogMetrics.MeterName)
            .AddPrometheusExporter());

    // ── Rate Limiting ─────────────────────────────────────────────────────────
    builder.Services.AddRateLimiter(options =>
    {
        var rl = builder.Configuration
            .GetSection($"{VectorCatalogOptions.SectionName}:RateLimit")
            .Get<RateLimitOptions>() ?? new RateLimitOptions();

        options.AddFixedWindowLimiter("search", config =>
        {
            config.PermitLimit = rl.PermitLimit;
            config.Window = TimeSpan.FromSeconds(rl.WindowSeconds);
            config.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            config.QueueLimit = rl.QueueLimit;
        });

        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    });

    // ── MVC / API ─────────────────────────────────────────────────────────────
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new()
        {
            Title = "Vector Catalog API",
            Version = "v1",
            Description = "ANN search service over 100M+ vectors with Delta Lake ingestion pipeline"
        });
        var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
        var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
        if (File.Exists(xmlPath))
            c.IncludeXmlComments(xmlPath);
    });

    // ── Build App ─────────────────────────────────────────────────────────────
    var app = builder.Build();

    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseRouting();
    app.UseRateLimiter();
    app.UseAuthorization();

    // Prometheus metrics endpoint
    app.MapPrometheusScrapingEndpoint("/metrics");

    app.MapControllers();

    // Health endpoints (also exposed via HealthController, but ASP.NET middleware adds /health/live etc.)
    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = _ => false
    });
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready")
    });

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
