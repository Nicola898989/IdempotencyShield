using IdempotencyShield.EntityFrameworkCore;
using IdempotencyShield.EntityFrameworkCore.Entities;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using Xunit;

namespace IdempotencyShield.Tests.Unit;

public class EfCoreStoreUnitTests
{
    private readonly Mock<TestIdempotencyContext> _mockContext;
    private readonly EfCoreIdempotencyStore<TestIdempotencyContext> _store;
    private readonly Mock<DbSet<IdempotencyLockEntity>> _mockDbSet;

    public EfCoreStoreUnitTests()
    {
        var options = new DbContextOptionsBuilder<TestIdempotencyContext>().Options;
        _mockContext = new Mock<TestIdempotencyContext>(options);
        
        // We initialize the DbSet but we'll override for specific tests using BuildMock
        _mockDbSet = new Mock<DbSet<IdempotencyLockEntity>>();
        
        _mockContext.Setup(c => c.IdempotencyLocks).Returns(_mockDbSet.Object);
        _mockContext.Setup(c => c.Set<IdempotencyLockEntity>()).Returns(_mockDbSet.Object);

        _store = new EfCoreIdempotencyStore<TestIdempotencyContext>(_mockContext.Object);
    }

    [Fact]
    public async Task TryAcquireLockAsync_WhenTakeoverFailsWithConcurrency_ShouldReturnFalse()
    {
        // Scenario:
        // 1. Lock exists but is expired.
        // 2. We try to update it (Takeover).
        // 3. SaveChangesAsync throws DbUpdateConcurrencyException (someone else took it).
        // 4. Result should be false.

        // Arrange
        var key = "lock-takeover-race-failure";
        var expiredLock = new IdempotencyLockEntity 
        { 
            Key = key, 
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1), // Expired
            OwnerId = "other-owner"
        };

        // Create a List to mock the DB content
        var data = new List<IdempotencyLockEntity> { expiredLock };
        
        // Use MockQueryable to setup the DbSet behavior for Async queries
        var mockSet = data.AsQueryable().BuildMockDbSet();
        
        _mockContext.Setup(c => c.IdempotencyLocks).Returns(mockSet.Object);
        _mockContext.Setup(c => c.Set<IdempotencyLockEntity>()).Returns(mockSet.Object);

        // Mock SaveChangesAsync to throw Concurrency Exception
        _mockContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateConcurrencyException("Race condition simulation", 
                new List<Microsoft.EntityFrameworkCore.Update.IUpdateEntry>()));

        // Act
        var result = await _store.TryAcquireLockAsync(key);

        // Assert
        Assert.False(result);
        
        // Verify SaveChanges was indeed called (meaning we tried to take over)
        _mockContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
