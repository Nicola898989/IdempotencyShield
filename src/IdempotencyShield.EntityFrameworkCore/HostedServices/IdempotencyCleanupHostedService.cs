using IdempotencyShield.EntityFrameworkCore.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IdempotencyShield.EntityFrameworkCore.HostedServices;

/// <summary>
/// A background service that periodically cleans up expired idempotency records and locks from the database.
/// </summary>
public class IdempotencyCleanupHostedService<TContext> : BackgroundService 
    where TContext : DbContext, IIdempotencyDbContext
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<IdempotencyCleanupHostedService<TContext>> _logger;
    private readonly TimeSpan _cleanupInterval;

    public IdempotencyCleanupHostedService(
        IServiceProvider serviceProvider,
        ILogger<IdempotencyCleanupHostedService<TContext>> logger,
        TimeSpan cleanupInterval)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _cleanupInterval = cleanupInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Idempotency Cleanup Service started. Interval: {Interval}", _cleanupInterval);

        using var timer = new PeriodicTimer(_cleanupInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CleanupExpiredRecordsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while cleaning up expired idempotency records.");
            }
        }
    }

    private async Task CleanupExpiredRecordsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var now = DateTime.UtcNow;

        _logger.LogDebug("Starting cleanup of expired idempotency records...");

#if NET7_0_OR_GREATER
        // Efficient bulk delete for .NET 7+ / EF Core 7+
        var deletedRecords = await context.IdempotencyRecords
            .Where(r => r.ExpiresAt < now)
            .ExecuteDeleteAsync(cancellationToken);
        
        var deletedLocks = await context.IdempotencyLocks
            .Where(l => l.ExpiresAt < now)
            .ExecuteDeleteAsync(cancellationToken);
#else
        // Fallback for .NET 6 / EF Core 6 (Fetch and Delete)
        // Only fetch keys to minimize memory usage
        var expiredRecords = await context.IdempotencyRecords
            .Where(r => r.ExpiresAt < now)
            .ToListAsync(cancellationToken);

        if (expiredRecords.Any())
        {
            context.IdempotencyRecords.RemoveRange(expiredRecords);
        }

        var expiredLocks = await context.IdempotencyLocks
            .Where(l => l.ExpiresAt < now)
            .ToListAsync(cancellationToken);

        if (expiredLocks.Any())
        {
             context.IdempotencyLocks.RemoveRange(expiredLocks);
        }

        var deletedRecords = expiredRecords.Count;
        var deletedLocks = expiredLocks.Count;
        
        if (deletedRecords > 0 || deletedLocks > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
#endif

        if (deletedRecords > 0 || deletedLocks > 0)
        {
            _logger.LogInformation("Cleanup completed. Deleted {RecordCount} records and {LockCount} locks.", deletedRecords, deletedLocks);
        }
        else
        {
            _logger.LogDebug("Cleanup completed. No expired records found.");
        }
    }
}
