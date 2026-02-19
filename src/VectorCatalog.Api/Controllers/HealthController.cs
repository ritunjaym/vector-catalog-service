using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using VectorCatalog.Api.Models;

namespace VectorCatalog.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class HealthController : ControllerBase
{
    private readonly HealthCheckService _healthCheckService;
    private readonly ServiceInfo _serviceInfo;

    public HealthController(HealthCheckService healthCheckService, ServiceInfo serviceInfo)
    {
        _healthCheckService = healthCheckService;
        _serviceInfo = serviceInfo;
    }

    /// <summary>Liveness probe — returns 200 if process is alive.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Health()
    {
        return Ok(new
        {
            status = "healthy",
            service = _serviceInfo.Name,
            version = _serviceInfo.Version,
            uptime = _serviceInfo.Uptime.ToString(@"hh\:mm\:ss"),
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>Readiness probe — returns 200 only if all dependencies are healthy.</summary>
    [HttpGet("/ready")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<object>> Ready(CancellationToken cancellationToken)
    {
        var report = await _healthCheckService.CheckHealthAsync(cancellationToken);
        var statusCode = report.Status == HealthStatus.Healthy ? 200 : 503;

        return StatusCode(statusCode, new
        {
            status = report.Status.ToString().ToLower(),
            checks = report.Entries.ToDictionary(
                kvp => kvp.Key,
                kvp => new
                {
                    status = kvp.Value.Status.ToString().ToLower(),
                    description = kvp.Value.Description,
                    duration = kvp.Value.Duration.TotalMilliseconds
                })
        });
    }
}
