using System.Text.Json;
using IdempotencyShield.EntityFrameworkCore.Entities;
using IdempotencyShield.Models;
using IdempotencyShield.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Primitives;

namespace IdempotencyShield.EntityFrameworkCore;

public class EfCoreIdempotencyStore<TContext> : IIdempotencyStore
    where TContext : DbContext, IIdempotencyDbContext
{
    private readonly TContext _context;
    // Unique ID for this request execution instance to ensure we own the lock we release
    // Using AsyncLocal so it flows with the async context but is unique per request if store is scoped
    private readonly string _ownerId = Guid.NewGuid().ToString();

    public EfCoreIdempotencyStore(TContext context)
    {
        _context = context;
    }

    public async Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var entity = await _context.IdempotencyRecords
            .FirstOrDefaultAsync(r => r.Key == key, cancellationToken);

        if (entity == null)
        {
            return null;
        }

        // Lazy cleanup: if found but expired, treat as null (and maybe delete?)
        // Deleting on read changes state (Get is usually side-effect freeish), but for cache it's fine.
        // Let's just return null and let SaveAsync overwrite or background job cleanup.
        if (DateTime.UtcNow > entity.ExpiresAt)
        {
            return null;
        }

        Dictionary<string, StringValues>? headers = null;
        if (!string.IsNullOrEmpty(entity.HeadlinesJson))
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string[]>>(entity.HeadlinesJson);
                if (dict != null)
                {
                    headers = new Dictionary<string, StringValues>();
                    foreach (var kvp in dict)
                    {
                        headers[kvp.Key] = new StringValues(kvp.Value);
                    }
                }
            }
            catch
            {
                // Ignore serialization errors
            }
        }

        return new IdempotencyRecord
        {
            StatusCode = entity.StatusCode,
            Body = entity.Body ?? Array.Empty<byte>(),
            Headers = headers ?? new Dictionary<string, StringValues>(),
            CreatedAt = entity.CreatedAt,
            RequestBodyHash = entity.RequestBodyHash
        };
    }

    public async Task SaveAsync(string key, IdempotencyRecord record, int expiryMinutes, CancellationToken cancellationToken = default)
    {
        var headersDict = new Dictionary<string, string[]>();
        foreach (var h in record.Headers)
        {
            headersDict[h.Key] = h.Value.Select(v => v ?? string.Empty).ToArray();
        }

        var json = JsonSerializer.Serialize(headersDict);
        var expiresAt = DateTime.UtcNow.AddMinutes(expiryMinutes);

        var entity = new IdempotencyRecordEntity
        {
            Key = key,
            StatusCode = record.StatusCode,
            Body = record.Body,
            HeadlinesJson = json,
            CreatedAt = record.CreatedAt, // Use original created at
            ExpiresAt = expiresAt,
            RequestBodyHash = record.RequestBodyHash
        };

        // Upsert logic
        var existing = await _context.IdempotencyRecords.FirstOrDefaultAsync(r => r.Key == key, cancellationToken);
        if (existing != null)
        {
            existing.StatusCode = record.StatusCode;
            existing.Body = record.Body;
            existing.HeadlinesJson = json;
            existing.ExpiresAt = expiresAt;
            existing.RequestBodyHash = record.RequestBodyHash;
            // Keep CreatedAt? Or update? usually keep.
        }
        else
        {
            _context.IdempotencyRecords.Add(entity);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private IDbContextTransaction? _currentTransaction;

    public async Task<bool> TryAcquireLockAsync(string key, int lockExpirationMilliseconds, int lockWaitTimeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        var timeoutSpan = TimeSpan.FromMilliseconds(lockWaitTimeoutMilliseconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Calculate remaining time for this attempt (if we want to bound the DB operation)
            // But usually we just check after the attempt.
            
            // Use a transaction to ensure strict serialization and visibility.
            _currentTransaction = await _context.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
            
            bool lockAcquired = false;
            bool shouldRetry = false;

            try
            {
                var expiresAt = DateTime.UtcNow.AddMilliseconds(lockExpirationMilliseconds);

                var existingLock = await _context.IdempotencyLocks
                    .FirstOrDefaultAsync(l => l.Key == key, cancellationToken);

                if (existingLock != null)
                {
                    if (existingLock.ExpiresAt < DateTime.UtcNow)
                    {
                        // Expired - take over
                        existingLock.ExpiresAt = expiresAt;
                        existingLock.OwnerId = _ownerId;
                        
                        await _context.SaveChangesAsync(cancellationToken);
                        lockAcquired = true;
                    }
                    else
                    {
                        // Active lock found
                        shouldRetry = true;
                    }
                }
                else
                {
                    // No lock found - try to create
                    var newLock = new IdempotencyLockEntity
                    {
                        Key = key,
                        ExpiresAt = expiresAt,
                        OwnerId = _ownerId
                    };
                    
                    _context.IdempotencyLocks.Add(newLock);
                    await _context.SaveChangesAsync(cancellationToken);

                    // Safety Check: Did someone finish the job while we were waiting for the DB lock?
                    var recordExists = await _context.IdempotencyRecords.AsNoTracking().AnyAsync(r => r.Key == key, cancellationToken);
                    if (recordExists)
                    {
                        // Work is done. Release the lock and return false.
                        _context.IdempotencyLocks.Remove(newLock);
                        await _context.SaveChangesAsync(cancellationToken);
                        
                        await _currentTransaction.CommitAsync(cancellationToken);
                        await _currentTransaction.DisposeAsync();
                        _currentTransaction = null;
                        
                        return false;
                    }

                    lockAcquired = true;
                }
            }
            catch (DbUpdateException)
            {
                // Concurrency violation (someone else inserted/updated)
                shouldRetry = true;
            }
            catch (Exception)
            {
                 // Unknown error
                 if (_currentTransaction != null)
                 {
                     await _currentTransaction.DisposeAsync();
                     _currentTransaction = null;
                 }
                 throw;
            }

            if (lockAcquired)
            {
                return true;
            }
            
            // Cleanup failed attempt
            if (_currentTransaction != null)
            {
                await _currentTransaction.DisposeAsync();
                _currentTransaction = null;
            }

            // Check if we timed out
            if (lockWaitTimeoutMilliseconds <= 0 || (DateTime.UtcNow - startTime) >= timeoutSpan)
            {
                return false;
            }

            // Wait before retrying (Spin-Wait)
            var delay = Random.Shared.Next(15, 50);
            await Task.Delay(delay, cancellationToken);
        }
    }

    public async Task ReleaseLockAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            // Safe release: only delete if WE own it
#if NET7_0_OR_GREATER
            await _context.IdempotencyLocks
                .Where(l => l.Key == key && l.OwnerId == _ownerId)
                .ExecuteDeleteAsync(cancellationToken);
#else
            var lockEntity = await _context.IdempotencyLocks
                .FirstOrDefaultAsync(l => l.Key == key, cancellationToken);

            if (lockEntity != null && lockEntity.OwnerId == _ownerId)
            {
                _context.IdempotencyLocks.Remove(lockEntity);
                await _context.SaveChangesAsync(cancellationToken);
            }
#endif
            
            // If we have an open transaction, commit it now to persist the result (SaveAsync) and the lock release.
            if (_currentTransaction != null)
            {
                await _currentTransaction.CommitAsync(cancellationToken);
            }
        }
        catch (Exception)
        {
             // If release fails, we still want to dispose (rollback) to avoid hanging connections, 
             // though data might be lost or locked until timeout.
             // Given flow, rollback might be safer if commit fails.
             throw;
        }
        finally
        {
            if (_currentTransaction != null)
            {
                await _currentTransaction.DisposeAsync();
                _currentTransaction = null;
            }
        }
    }
}
