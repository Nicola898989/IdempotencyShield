using IdempotencyShield.EntityFrameworkCore;
using IdempotencyShield.EntityFrameworkCore.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;

namespace IdempotencyShield.Tests;

public class TestIdempotencyContext : DbContext, IIdempotencyDbContext
{
    public TestIdempotencyContext(DbContextOptions<TestIdempotencyContext> options) : base(options) { }

    public virtual DbSet<IdempotencyRecordEntity> IdempotencyRecords { get; set; }
    public virtual DbSet<IdempotencyLockEntity> IdempotencyLocks { get; set; }

    public virtual Task<IDbContextTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
    {
        return Database.BeginTransactionAsync(isolationLevel, cancellationToken);
    }
}
