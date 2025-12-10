using IdempotencyShield.EntityFrameworkCore;
using IdempotencyShield.EntityFrameworkCore.Entities;
using Microsoft.EntityFrameworkCore;

namespace IdempotencyShield.Tests;

public class TestIdempotencyContext : DbContext, IIdempotencyDbContext
{
    public TestIdempotencyContext(DbContextOptions<TestIdempotencyContext> options) : base(options) { }

    public virtual DbSet<IdempotencyRecordEntity> IdempotencyRecords { get; set; }
    public virtual DbSet<IdempotencyLockEntity> IdempotencyLocks { get; set; }
}
