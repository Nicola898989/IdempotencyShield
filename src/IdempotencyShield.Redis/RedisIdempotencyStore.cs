using System.Text.Json;
using System.Threading;
using IdempotencyShield.Models;
using IdempotencyShield.Storage;
using StackExchange.Redis;

namespace IdempotencyShield.Redis;

/// <summary>
/// Redis-backed implementation of <see cref="IIdempotencyStore"/> using StackExchange.Redis.
/// Provides distributed caching and locking suitable for multi-instance production deployments.
/// </summary>
public class RedisIdempotencyStore : IIdempotencyStore
{
    private readonly IConnectionMultiplexer _redis;
    private readonly JsonSerializerOptions _jsonOptions;
    private const string CacheKeyPrefix = "idempotency:cache:";
    private const string LockKeyPrefix = "idempotency:lock:";
    private const int LockExpirySeconds = 30; // Max time a lock can be held

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisIdempotencyStore"/> class.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    public RedisIdempotencyStore(IConnectionMultiplexer redis)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
        _jsonOptions.Converters.Add(new Formatting.StringValuesJsonConverter());
    }

    /// <summary>
    /// Retrieves a cached idempotency record from Redis.
    /// </summary>
    public async Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var cacheKey = CacheKeyPrefix + key;

        var json = await db.StringGetAsync(cacheKey);
        
        if (json.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            var record = JsonSerializer.Deserialize<IdempotencyRecord>(json.ToString(), _jsonOptions);
            return record;
        }
        catch (JsonException)
        {
            // Corrupted data - remove it
            await db.KeyDeleteAsync(cacheKey);
            return null;
        }
    }

    /// <summary>
    /// Saves an idempotency record to Redis with expiration.
    /// </summary>
    public async Task SaveAsync(string key, IdempotencyRecord record, int expiryMinutes, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var cacheKey = CacheKeyPrefix + key;
        
        var json = JsonSerializer.Serialize(record, _jsonOptions);
        
        // Ensure expiry is positive
        TimeSpan? expiry = expiryMinutes > 0 ? TimeSpan.FromMinutes(expiryMinutes) : null;

        await db.StringSetAsync(cacheKey, json, expiry);
    }

    private readonly AsyncLocal<string?> _currentLockValue = new();

    /// <summary>
    /// Attempts to acquire a distributed lock using Redis SET NX (SET if Not eXists).
    /// Returns true if the lock was acquired, false if another process holds it.
    /// If lockWaitTimeoutMilliseconds > 0, it retries until the timeout is reached.
    /// </summary>
    public async Task<bool> TryAcquireLockAsync(string key, int lockExpirationMilliseconds, int lockWaitTimeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var lockKey = LockKeyPrefix + key;
        
        // Generate a unique value for this specific lock acquisition attempt
        var lockValue = Guid.NewGuid().ToString();
        
        // Ensure strictly positive expiry for Redis SET command
        var safeExpiry = Math.Max(1, lockExpirationMilliseconds);
        var expirySpan = TimeSpan.FromMilliseconds(safeExpiry);

        var startTime = DateTime.UtcNow;
        var timeoutSpan = TimeSpan.FromMilliseconds(lockWaitTimeoutMilliseconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Use SET with NX (Not eXists) flag and expiry for distributed locking
            // The lock expires automatically after lockExpirationMilliseconds to prevent deadlocks
            var acquired = await db.StringSetAsync(
                lockKey,
                lockValue,
                expirySpan,
                When.NotExists
            );

            if (acquired)
            {
                // Store the lock value in the current async context so we can release it safely later
                _currentLockValue.Value = lockValue;
                return true;
            }

            // If we don't want to wait, or if we ran out of time
            if (lockWaitTimeoutMilliseconds <= 0 || (DateTime.UtcNow - startTime) >= timeoutSpan)
            {
                return false;
            }

            // Wait before retrying (Spin-Wait)
            // Use a small delay with jitter to prevent thundering herd
            var delay = Random.Shared.Next(15, 50);
            await Task.Delay(delay, cancellationToken);
        }
    }

    /// <summary>
    /// Releases the distributed lock safely using a Lua script.
    /// Only deletes the key if the value matches our lock value.
    /// </summary>
    public async Task ReleaseLockAsync(string key, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var lockKey = LockKeyPrefix + key;
        var lockValue = _currentLockValue.Value;

        // If we don't have a lock value, we didn't acquire it (or context was lost), so we shouldn't delete anything
        if (string.IsNullOrEmpty(lockValue))
        {
            return;
        }

        // Lua script to check value before deleting
        // This prevents deleting a lock that has been acquired by another process (after our expiry)
        const string script = @"
            if redis.call('get', KEYS[1]) == ARGV[1] then
                return redis.call('del', KEYS[1])
            else
                return 0
            end";

        await db.ScriptEvaluateAsync(script, new RedisKey[] { lockKey }, new RedisValue[] { lockValue });
        
        // Clear the context
        _currentLockValue.Value = null;
    }

    // Removed GetLockValue helper as we generate unique GUIDs now
}
