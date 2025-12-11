using IdempotencyShield.EntityFrameworkCore;
using IdempotencyShield.EntityFrameworkCore.Entities;
using IdempotencyShield.EntityFrameworkCore.HostedServices;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IdempotencyShield.Tests.Integration;

public class CleanupServiceTests
{
    private class TestDbContext : DbContext, IIdempotencyDbContext
    {
        public DbSet<IdempotencyRecordEntity> IdempotencyRecords { get; set; }
        public DbSet<IdempotencyLockEntity> IdempotencyLocks { get; set; }

        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }

        public Task<IDbContextTransaction> BeginTransactionAsync(System.Data.IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
        {
             return Database.BeginTransactionAsync(isolationLevel, cancellationToken);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ShouldDeleteExpiredRecords()
    {
        // Arrange
        var dbName = $"idempotency_test_{Guid.NewGuid()}.db";
        var connectionString = $"Data Source={dbName}";

        try 
        {
            var services = new ServiceCollection();
            services.AddDbContext<IIdempotencyDbContext, TestDbContext>(options =>
                options.UseSqlite(connectionString));
            
            var serviceProvider = services.BuildServiceProvider();
            var context = (TestDbContext)serviceProvider.GetRequiredService<IIdempotencyDbContext>();
            context.Database.EnsureCreated();

            // Seed Data
            context.IdempotencyRecords.AddRange(
                new IdempotencyRecordEntity { Key = "expired-1", ExpiresAt = DateTime.UtcNow.AddHours(-2), CreatedAt = DateTime.UtcNow, StatusCode=200, Body = new byte[0] },
                new IdempotencyRecordEntity { Key = "valid-1", ExpiresAt = DateTime.UtcNow.AddHours(1), CreatedAt = DateTime.UtcNow, StatusCode=200, Body = new byte[0] }
            );
            context.IdempotencyLocks.AddRange(
                 new IdempotencyLockEntity { Key = "expired-lock", ExpiresAt = DateTime.UtcNow.AddMinutes(-5), OwnerId = "test" },
                 new IdempotencyLockEntity { Key = "valid-lock", ExpiresAt = DateTime.UtcNow.AddMinutes(5), OwnerId = "test" }
            );
            await context.SaveChangesAsync();

            // Act
            // We start the service, wait a bit, then stop it.
            using var service = new IdempotencyCleanupHostedService<TestDbContext>(
                serviceProvider, 
                NullLogger<IdempotencyCleanupHostedService<TestDbContext>>.Instance, 
                TimeSpan.FromMilliseconds(50));

            // Act
            // We start the service, wait a bit, then stop it.
            var cts = new CancellationTokenSource();
            var checkTask = service.StartAsync(cts.Token);
            
            // Wait for at least one cycle
            await Task.Delay(500); // Increased delay for IO
            cts.Cancel();
            await service.StopAsync(CancellationToken.None);

            // Assert
            // Re-fetch context to be sure? Same instance is fine for tracking, but let's reload
            context.ChangeTracker.Clear();
            
            var records = await context.IdempotencyRecords.ToListAsync();
            Assert.Single(records);
            Assert.Equal("valid-1", records[0].Key);

            var locks = await context.IdempotencyLocks.ToListAsync();
            Assert.Single(locks);
            Assert.Equal("valid-lock", locks[0].Key);
        }
        finally
        {
            if (File.Exists(dbName))
            {
                try { File.Delete(dbName); } catch {}
            }
        }
    }
}
