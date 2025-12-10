using IdempotencyShield.Models;

namespace IdempotencyShield.Storage;

/// <summary>
/// Abstraction for storing and retrieving idempotency records with distributed locking support.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Retrieves a cached idempotency record by key.
    /// </summary>
    /// <param name="key">The idempotency key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached record if found, otherwise null.</returns>
    Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves an idempotency record to the store.
    /// </summary>
    /// <param name="key">The idempotency key.</param>
    /// <param name="record">The record to save.</param>
    /// <param name="expiryMinutes">How long to keep the record before expiration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(string key, IdempotencyRecord record, int expiryMinutes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to acquire a distributed lock for the given key.
    /// </summary>
    /// <param name="key">The idempotency key.</param>
    /// <param name="lockTimeoutMilliseconds">The maximum time to wait for the lock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the lock was acquired, false if another process holds it.</returns>
    Task<bool> TryAcquireLockAsync(string key, int lockTimeoutMilliseconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the distributed lock for the given key.
    /// </summary>
    /// <param name="key">The idempotency key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ReleaseLockAsync(string key, CancellationToken cancellationToken = default);
}
