using Microsoft.AspNetCore.Mvc;
using VectorCatalog.Api.Models;
using VectorCatalog.Api.Protos;

namespace VectorCatalog.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
public class IndexController : ControllerBase
{
    private readonly IndexService.IndexServiceClient _indexClient;
    private readonly ILogger<IndexController> _logger;

    public IndexController(IndexService.IndexServiceClient indexClient, ILogger<IndexController> logger)
    {
        _indexClient = indexClient;
        _logger = logger;
    }

    /// <summary>Returns info about all loaded FAISS index shards.</summary>
    [HttpGet("info")]
    [ProducesResponseType(typeof(IEnumerable<IndexShardInfo>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<IndexShardInfo>>> GetInfo(
        [FromQuery] string? shardKey,
        CancellationToken cancellationToken)
    {
        var response = await _indexClient.GetIndexInfoAsync(
            new IndexInfoRequest { ShardKey = shardKey ?? "" },
            cancellationToken: cancellationToken);

        var shards = response.Shards.Select(s => new IndexShardInfo
        {
            ShardKey = s.ShardKey,
            TotalVectors = s.TotalVectors,
            Dimension = s.Dimension,
            IndexType = s.IndexType,
            IsTrained = s.IsTrained,
            IndexSizeBytes = s.IndexSizeBytes
        });

        return Ok(shards);
    }

    /// <summary>Triggers a hot reload of FAISS index shards without downtime.</summary>
    [HttpPost("reload")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<object>> ReloadIndex(
        [FromQuery] string? shardKey,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning("Index reload requested for shard: {ShardKey}", shardKey ?? "ALL");

        var response = await _indexClient.ReloadIndexAsync(
            new ReloadIndexRequest { ShardKey = shardKey ?? "" },
            cancellationToken: cancellationToken);

        if (!response.Success)
            return StatusCode(500, new { error = response.Message });

        return Ok(new { success = true, reloadedShards = response.ReloadedShards, message = response.Message });
    }
}
