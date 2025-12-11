using IdempotencyShield.EntityFrameworkCore;
using IdempotencyShield.EntityFrameworkCore.Entities;
using IdempotencyShield.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NSubstitute;
using Xunit;

namespace IdempotencyShield.Tests.Unit;

public class EfCoreDeadlockTests
{
    // Define a concrete context for testing to satisfy constraint: where TContext : DbContext, IIdempotencyDbContext
    public class TestDbContext : DbContext, IIdempotencyDbContext
    {
        public DbSet<IdempotencyRecordEntity> IdempotencyRecords { get; set; } = null!;
        public DbSet<IdempotencyLockEntity> IdempotencyLocks { get; set; } = null!;

        public TestDbContext(DbContextOptions options) : base(options) { }

        public Task<IDbContextTransaction> BeginTransactionAsync(System.Data.IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
        {
             // InMemory provider doesn't support transactions, return dummy
             if (Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
             {
                 return Task.FromResult<IDbContextTransaction>(new DummyTransaction());
             }
             return Database.BeginTransactionAsync(isolationLevel, cancellationToken);
        }

        private class DummyTransaction : IDbContextTransaction
        {
            public Guid TransactionId { get; } = Guid.NewGuid();
            public void Commit() { }
            public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public void Rollback() { }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    // Manual mock to avoid NSubstitute complexities with DbContext virtual methods and proxies
    public class MockDbContext : TestDbContext
    {
        public int SaveChangesCalls { get; private set; }
        public MockDbContext(DbContextOptions options) : base(options) { }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalls++;
            var innerEx = new DbUpdateException("Deadlock simulation");
            var outerEx = new InvalidOperationException("EF Core Wrapped Exception", innerEx);
            throw outerEx;
        }
    }

    [Fact]
    public async Task TryAcquireLockAsync_WhenDeadlockExhaustsRetries_ShouldReturnFalse()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var context = new MockDbContext(options);
        var store = new EfCoreIdempotencyStore<MockDbContext>(context);
        
        // Act
        // Must provide enough timeout for retries. 
        // Logic: 
        // 1. Try -> SaveChangesAsync (Throws) -> Catch -> shouldRetry=true
        // 2. Delay
        // 3. Try -> SaveChangesAsync (Throws) -> Catch -> shouldRetry=true
        // ...
        // Timeout
        
        var result = await store.TryAcquireLockAsync("deadlock-key", 1000, 1000, CancellationToken.None);

        // Assert
        Assert.False(result);
        
        // Assert multiple calls.
        // With 1000ms timeout and ~50ms delay, should be around 20 calls.
        // Assert at least 2 to ensure retry loop happened.
        Assert.True(context.SaveChangesCalls > 1, $"Expected > 1 calls, got {context.SaveChangesCalls}");
    }
}
