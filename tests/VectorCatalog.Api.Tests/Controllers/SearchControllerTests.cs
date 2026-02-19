using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VectorCatalog.Api.Controllers;
using VectorCatalog.Api.Models;
using VectorCatalog.Api.Services;
using Xunit;

namespace VectorCatalog.Api.Tests.Controllers;

public class SearchControllerTests
{
    private readonly Mock<ISearchService> _searchServiceMock = new();
    private readonly SearchController _sut;

    public SearchControllerTests()
    {
        _sut = new SearchController(_searchServiceMock.Object, NullLogger<SearchController>.Instance);
    }

    [Fact]
    public async Task Search_WithValidRequest_Returns200WithResults()
    {
        var request = new SearchRequest { Query = "taxi ride manhattan", TopK = 5 };
        var expected = new SearchResponse
        {
            Results = [new SearchResultItem { Id = 1, Score = 0.95f }],
            TotalLatencyMs = 42.0,
            CacheHit = false,
            QueryHash = "abc123"
        };
        _searchServiceMock.Setup(s => s.SearchAsync(It.IsAny<SearchRequest>(), It.IsAny<CancellationToken>()))
                          .ReturnsAsync(expected);

        var result = await _sut.Search(request, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<SearchResponse>().Subject;
        response.Results.Should().HaveCount(1);
        response.TotalLatencyMs.Should().Be(42.0);
    }

    [Fact]
    public async Task Search_CallsSearchService_WithCorrectParameters()
    {
        var request = new SearchRequest { Query = "test query", TopK = 10, ShardKey = "shard_a" };
        _searchServiceMock.Setup(s => s.SearchAsync(It.IsAny<SearchRequest>(), It.IsAny<CancellationToken>()))
                          .ReturnsAsync(new SearchResponse());

        await _sut.Search(request, CancellationToken.None);

        _searchServiceMock.Verify(s => s.SearchAsync(
            It.Is<SearchRequest>(r => r.Query == "test query" && r.TopK == 10 && r.ShardKey == "shard_a"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
