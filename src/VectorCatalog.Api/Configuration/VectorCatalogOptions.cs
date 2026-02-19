namespace VectorCatalog.Api.Configuration;

public class VectorCatalogOptions
{
    public const string SectionName = "VectorCatalog";

    public string SidecarGrpcAddress { get; set; } = "http://sidecar:50051";
    public RedisOptions Redis { get; set; } = new();
    public FaissOptions Faiss { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public RateLimitOptions RateLimit { get; set; } = new();
}

public class RedisOptions
{
    public string ConnectionString { get; set; } = "redis:6379";
    public int DefaultCacheTtlSeconds { get; set; } = 300;
    public string KeyPrefix { get; set; } = "vc:";
}

public class FaissOptions
{
    public int DefaultTopK { get; set; } = 10;
    public int DefaultNprobe { get; set; } = 10;
    public string DefaultShardKey { get; set; } = "nyc_taxi_2023";
}

public class StorageOptions
{
    public string Provider { get; set; } = "minio";
    public string MinioEndpoint { get; set; } = "http://minio:9000";
    public string MinioAccessKey { get; set; } = "minioadmin";
    public string MinioSecretKey { get; set; } = "minioadmin";
    public string BucketName { get; set; } = "vector-catalog";
    public string AdlsGen2Endpoint { get; set; } = "";
    public string AdlsGen2AccountName { get; set; } = "";
    public string AdlsGen2FileSystem { get; set; } = "vector-catalog";
}

public class RateLimitOptions
{
    public int PermitLimit { get; set; } = 100;
    public int WindowSeconds { get; set; } = 10;
    public int QueueLimit { get; set; } = 50;
}
