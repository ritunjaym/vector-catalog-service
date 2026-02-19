using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VectorCatalog.Api.Configuration;
using VectorCatalog.Api.Services;
using Xunit;

namespace VectorCatalog.Api.Tests.Services;

public class RedisCacheServiceTests
{
    private readonly Mock<IDistributedCache> _cacheMock = new();
    private readonly RedisCacheService _sut;

    public RedisCacheServiceTests()
    {
        var options = Options.Create(new VectorCatalogOptions
        {
            Redis = new RedisOptions { KeyPrefix = "test:", DefaultCacheTtlSeconds = 60 }
        });
        _sut = new RedisCacheService(_cacheMock.Object, options, NullLogger<RedisCacheService>.Instance);
    }

    [Fact]
    public async Task GetAsync_WhenKeyNotFound_ReturnsNull()
    {
        _cacheMock.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync((byte[]?)null);
        var result = await _sut.GetAsync<TestRecord>("missing-key");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WhenCacheThrows_ReturnsNull_WithoutThrowing()
    {
        _cacheMock.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new Exception("Redis down"));
        var act = async () => await _sut.GetAsync<TestRecord>("some-key");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void ComputeQueryHash_SameInputs_ReturnsSameHash()
    {
        var h1 = _sut.ComputeQueryHash("hello world", 10, "shard1");
        var h2 = _sut.ComputeQueryHash("hello world", 10, "shard1");
        h1.Should().Be(h2);
    }

    [Fact]
    public void ComputeQueryHash_DifferentInputs_ReturnDifferentHashes()
    {
        var h1 = _sut.ComputeQueryHash("hello world", 10, "shard1");
        var h2 = _sut.ComputeQueryHash("goodbye world", 10, "shard1");
        h1.Should().NotBe(h2);
    }

    [Fact]
    public void ComputeQueryHash_IsCaseInsensitive()
    {
        var h1 = _sut.ComputeQueryHash("Hello World", 10, "shard1");
        var h2 = _sut.ComputeQueryHash("hello world", 10, "shard1");
        h1.Should().Be(h2);
    }

    private record TestRecord(string Name, int Value);
}
