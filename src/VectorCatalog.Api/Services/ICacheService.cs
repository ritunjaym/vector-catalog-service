namespace VectorCatalog.Api.Services;

public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;
    Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken cancellationToken = default) where T : class;
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
    string ComputeQueryHash(string query, int topK, string shardKey);
}
