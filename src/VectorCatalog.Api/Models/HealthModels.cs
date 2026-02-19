namespace VectorCatalog.Api.Models;

public class ServiceInfo
{
    public string Name { get; set; } = "vector-catalog-api";
    public string Version { get; set; } = "0.1.0";
    public string Environment { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public TimeSpan Uptime => DateTime.UtcNow - StartedAt;
}
