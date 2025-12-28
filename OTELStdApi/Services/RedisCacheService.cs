using StackExchange.Redis;
using System.Text.Json;

namespace OTELStdApi.Services
{
    public class RedisCacheService : ICacheService
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly ILogger<RedisCacheService> _logger;
        private IDatabase _db;

        public RedisCacheService(IConnectionMultiplexer redis, ILogger<RedisCacheService> logger)
        {
            _redis = redis;
            _logger = logger;
            _db = _redis.GetDatabase();
        }

        public async Task<T?> GetAsync<T>(string key)
        {
            try
            {
                var value = await _db.StringGetAsync(key);
                
                if (value.IsNullOrEmpty)
                {
                    _logger.LogDebug("Cache miss for key: {CacheKey}", key);
                    return default;
                }

                _logger.LogDebug("Cache hit for key: {CacheKey}", key);
                var deserialized = JsonSerializer.Deserialize<T>(value.ToString());
                return deserialized;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting value from cache for key: {CacheKey}", key);
                return default;
            }
        }

        public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null)
        {
            try
            {
                var serialized = JsonSerializer.Serialize(value);
                // Convert TimeSpan to Expiration - use value property to unwrap nullable
                if (expiration.HasValue)
                {
                    await _db.StringSetAsync(key, serialized, expiration.Value);
                }
                else
                {
                    await _db.StringSetAsync(key, serialized);
                }
                _logger.LogDebug("Set cache value for key: {CacheKey}, expiration: {Expiration}", key, expiration);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting cache value for key: {CacheKey}", key);
            }
        }

        public async Task RemoveAsync(string key)
        {
            try
            {
                await _db.KeyDeleteAsync(key);
                _logger.LogDebug("Removed cache key: {CacheKey}", key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing cache key: {CacheKey}", key);
            }
        }

        public async Task<bool> ExistsAsync(string key)
        {
            try
            {
                return await _db.KeyExistsAsync(key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking cache key existence: {CacheKey}", key);
                return false;
            }
        }
    }
}
