using System.Collections.Concurrent;
using IdempotencyShield.Models;

namespace IdempotencyShield.Storage;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IIdempotencyStore"/> using concurrent collections and semaphores.
/// Suitable for single-instance testing and development. For production distributed systems, implement a Redis or database-backed store.
/// </summary>
public class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, (IdempotencyRecord Record, DateTime ExpiresAt)> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Retrieves a cached idempotency record if it exists and hasn't expired.
    /// </summary>
    public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        // Check if record exists and is not expired
        if (_cache.TryGetValue(key, out var entry))
        {
            if (DateTime.UtcNow < entry.ExpiresAt)
            {
                return Task.FromResult<IdempotencyRecord?>(entry.Record);
            }
            
            // Expired - remove it
            _cache.TryRemove(key, out _);
        }

        return Task.FromResult<IdempotencyRecord?>(null);
    }

    /// <summary>
    /// Saves an idempotency record with expiration.
    /// </summary>
    public Task SaveAsync(string key, IdempotencyRecord record, int expiryMinutes, CancellationToken cancellationToken = default)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(expiryMinutes);
        _cache[key] = (record, expiresAt);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Attempts to acquire a lock using a semaphore with a specified timeout.
    /// </summary>
    public Task<bool> TryAcquireLockAsync(string key, int lockExpirationMilliseconds, int lockWaitTimeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        // Use the wait timeout for the semaphore acquisition
        var acquired = semaphore.Wait(lockWaitTimeoutMilliseconds, cancellationToken);
        return Task.FromResult(acquired);
    }

    /// <summary>
    /// Releases the lock by releasing the semaphore.
    /// Also performs cleanup if the cache entry is expired to prevent memory leaks.
    /// </summary>
    public Task ReleaseLockAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_locks.TryGetValue(key, out var semaphore))
        {
            try
            {
                semaphore.Release();
            }
            catch (SemaphoreFullException)
            {
                // Lock was already released or never acquired - ignore
            }

            // Cleanup: Remove semaphore if cache entry is expired or doesn't exist
            // This prevents unbounded semaphore growth in long-running applications
            if (!_cache.TryGetValue(key, out var entry) || DateTime.UtcNow >= entry.ExpiresAt)
            {
                _locks.TryRemove(key, out _);
                semaphore.Dispose();
            }
        }

        return Task.CompletedTask;
    }
}
