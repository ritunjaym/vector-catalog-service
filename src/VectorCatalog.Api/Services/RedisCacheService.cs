using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using VectorCatalog.Api.Configuration;

namespace VectorCatalog.Api.Services;

public class RedisCacheService : ICacheService
{
    private readonly IDistributedCache _cache;
    private readonly RedisOptions _options;
    private readonly ILogger<RedisCacheService> _logger;

    public RedisCacheService(
        IDistributedCache cache,
        IOptions<VectorCatalogOptions> options,
        ILogger<RedisCacheService> logger)
    {
        _cache = cache;
        _options = options.Value.Redis;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        var fullKey = _options.KeyPrefix + key;
        try
        {
            var bytes = await _cache.GetAsync(fullKey, cancellationToken);
            if (bytes is null)
            {
                _logger.LogDebug("Cache MISS: {Key}", fullKey);
                return null;
            }

            _logger.LogDebug("Cache HIT: {Key}", fullKey);
            return JsonSerializer.Deserialize<T>(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache GET failed for key {Key}, treating as miss", fullKey);
            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken cancellationToken = default) where T : class
    {
        var fullKey = _options.KeyPrefix + key;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            var cacheoptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl ?? TimeSpan.FromSeconds(_options.DefaultCacheTtlSeconds)
            };
            await _cache.SetAsync(fullKey, bytes, cacheoptions, cancellationToken);
            _logger.LogDebug("Cache SET: {Key}, TTL={Ttl}", fullKey, cacheoptions.AbsoluteExpirationRelativeToNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache SET failed for key {Key}, continuing without cache", fullKey);
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var fullKey = _options.KeyPrefix + key;
        try
        {
            await _cache.RemoveAsync(fullKey, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache DELETE failed for key {Key}", fullKey);
            return false;
        }
    }

    public string ComputeQueryHash(string query, int topK, string shardKey)
    {
        var input = $"{query.ToLowerInvariant().Trim()}|{topK}|{shardKey}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes)[..16]; // first 16 hex chars
    }
}
