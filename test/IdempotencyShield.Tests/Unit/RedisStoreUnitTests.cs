using System.Text.Json;
using IdempotencyShield.Redis;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace IdempotencyShield.Tests.Unit;

public class RedisStoreUnitTests
{
    private readonly Mock<IConnectionMultiplexer> _mockMultiplexer;
    private readonly Mock<IDatabase> _mockDatabase;
    private readonly RedisIdempotencyStore _store;

    public RedisStoreUnitTests()
    {
        _mockMultiplexer = new Mock<IConnectionMultiplexer>();
        _mockDatabase = new Mock<IDatabase>();

        _mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_mockDatabase.Object);

        _store = new RedisIdempotencyStore(_mockMultiplexer.Object);
    }

    [Fact]
    public async Task GetAsync_WhenJsonIsCorrupted_ShouldDeleteKeyAndReturnNull()
    {
        // Arrange
        var key = "corrupted-key";
        var redisKey = new RedisKey("idempotency:cache:" + key);
        var corruptedJson = "{ invalid-json }"; // Malformed JSON

        // Mock Redis returning corrupted string
        _mockDatabase.Setup(db => db.StringGetAsync(redisKey, It.IsAny<CommandFlags>()))
            .ReturnsAsync(corruptedJson);

        // Act
        var result = await _store.GetAsync(key);

        // Assert
        Assert.Null(result);

        // Verify that the key was deleted
        _mockDatabase.Verify(db => db.KeyDeleteAsync(redisKey, It.IsAny<CommandFlags>()), Times.Once);
    }
}
