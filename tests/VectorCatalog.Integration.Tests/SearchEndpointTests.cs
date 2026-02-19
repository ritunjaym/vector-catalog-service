using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Redis;
using VectorCatalog.Api.Models;
using VectorCatalog.Api.Services;
using Xunit;

namespace VectorCatalog.Integration.Tests;

/// <summary>
/// Integration tests for the /search endpoint.
/// Uses Testcontainers to spin up a real Redis instance.
/// Mocks the gRPC sidecar to avoid needing Python running.
/// </summary>
public class SearchEndpointTests : IAsyncLifetime
{
    private readonly RedisContainer _redis;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    static SearchEndpointTests()
    {
        // When dotnet test runs inside a Docker container (Docker-in-Docker), two
        // problems arise:
        //   1. Ryuk (the Testcontainers resource reaper) cannot reach its own
        //      container port → 60s timeout → ResourceReaperException.
        //   2. On Docker Desktop (macOS/Windows), Redis is mapped to a host port
        //      that is not reachable via "localhost" from within the SDK container;
        //      "host.docker.internal" is the correct gateway.
        // CI runners execute dotnet test natively (no /.dockerenv) so these
        // overrides are never applied there.
        if (File.Exists("/.dockerenv"))
        {
            Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
            Environment.SetEnvironmentVariable("TESTCONTAINERS_HOST_OVERRIDE", "host.docker.internal");
        }
    }

    public SearchEndpointTests()
    {
        _redis = new RedisBuilder()
            .WithImage("redis:7.2-alpine")
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("VectorCatalog:Redis:ConnectionString", _redis.GetConnectionString());
                builder.UseSetting("VectorCatalog:SidecarGrpcAddress", "http://localhost:50051");

                builder.ConfigureServices(services =>
                {
                    // Replace real SearchService with a stub that returns predictable results
                    var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ISearchService));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddScoped<ISearchService, StubSearchService>();
                });
            });

        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory != null) await _factory.DisposeAsync();
        await _redis.DisposeAsync();
    }

    [Fact]
    public async Task Search_ValidRequest_Returns200WithResults()
    {
        var response = await _client!.PostAsJsonAsync("/api/v1/search",
            new { query = "taxi ride manhattan", topK = 5 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SearchResponse>();
        body.Should().NotBeNull();
        body!.Results.Should().HaveCount(3);
        body.QueryHash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Search_EmptyQuery_Returns400()
    {
        var response = await _client!.PostAsJsonAsync("/api/v1/search",
            new { query = "", topK = 5 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_ResponseContainsCorrelationIdHeader()
    {
        var response = await _client!.PostAsJsonAsync("/api/v1/search",
            new { query = "test", topK = 5 });

        response.Headers.Should().ContainKey("X-Correlation-ID");
    }

    [Fact]
    public async Task Search_SecondIdenticalRequest_ReturnsCacheHit()
    {
        var payload = new { query = "cache test query", topK = 5 };

        // First request — cache miss
        var r1 = await _client!.PostAsJsonAsync("/api/v1/search", payload);
        var b1 = await r1.Content.ReadFromJsonAsync<SearchResponse>();
        b1!.CacheHit.Should().BeFalse();

        // Second identical request — should hit cache
        var r2 = await _client!.PostAsJsonAsync("/api/v1/search", payload);
        var b2 = await r2.Content.ReadFromJsonAsync<SearchResponse>();
        b2!.CacheHit.Should().BeTrue();
        b2.QueryHash.Should().Be(b1.QueryHash);
    }

    [Fact]
    public async Task HealthEndpoint_Returns200()
    {
        var response = await _client!.GetAsync("/health/live");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>Stub ISearchService for integration tests — no gRPC or FAISS needed.</summary>
internal class StubSearchService : ISearchService
{
    private readonly ICacheService _cache;
    public StubSearchService(ICacheService cache) => _cache = cache;

    public async Task<SearchResponse> SearchAsync(
        VectorCatalog.Api.Models.SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var hash = _cache.ComputeQueryHash(request.Query, request.TopK, request.ShardKey ?? "default");

        var cached = await _cache.GetAsync<SearchResponse>(hash, cancellationToken);
        if (cached is not null)
        {
            cached.CacheHit = true;
            return cached;
        }

        var response = new SearchResponse
        {
            Results =
            [
                new SearchResultItem { Id = 1, Score = 0.95f },
                new SearchResultItem { Id = 2, Score = 0.88f },
                new SearchResultItem { Id = 3, Score = 0.82f }
            ],
            ShardKey = "test_shard",
            SearchLatencyMs = 12.5,
            TotalLatencyMs = 45.0,
            CacheHit = false,
            QueryHash = hash
        };

        await _cache.SetAsync(hash, response, cancellationToken: cancellationToken);
        return response;
    }
}
