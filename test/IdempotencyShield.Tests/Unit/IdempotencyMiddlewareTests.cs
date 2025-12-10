using System.Text;
using IdempotencyShield.Attributes;
using IdempotencyShield.Configuration;
using IdempotencyShield.Middleware;
using IdempotencyShield.Models;
using IdempotencyShield.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace IdempotencyShield.Tests.Unit;

public class IdempotencyMiddlewareTests
{
    private readonly Mock<IIdempotencyStore> _mockStore;
    private readonly Mock<RequestDelegate> _mockNext;
    private readonly IdempotencyOptions _options;
    private readonly IdempotencyMiddleware _middleware;

    public IdempotencyMiddlewareTests()
    {
        _mockStore = new Mock<IIdempotencyStore>();
        _mockNext = new Mock<RequestDelegate>();
        _options = new IdempotencyOptions { HeaderName = "Idempotency-Key" };
        
        var optionsMock = new Mock<IOptions<IdempotencyOptions>>();
        optionsMock.Setup(o => o.Value).Returns(_options);

        _middleware = new IdempotencyMiddleware(_mockNext.Object, optionsMock.Object);
    }

    [Fact]
    public async Task InvokeAsync_WhenControllerReturnsError_ShouldNotSaveToStore()
    {
        // Arrange
        var context = CreateContextWithIdempotency("test-key-error");
        
        // Mock store: No existing key, lock acquired successfully
        _mockStore.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdempotencyRecord?)null);
        
        _mockStore.Setup(s => s.TryAcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Mock Next: Controller throws or returns 500
        _mockNext.Setup(next => next(It.IsAny<HttpContext>()))
            .Callback<HttpContext>(ctx => ctx.Response.StatusCode = 500)
            .Returns(Task.CompletedTask);

        // Act
        await _middleware.InvokeAsync(context, _mockStore.Object);

        // Assert
        // We expect SaveAsync to NEVER be called because status is 500
        _mockStore.Verify(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<IdempotencyRecord>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        
        // Lock should still be released
        _mockStore.Verify(s => s.ReleaseLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_RaceCondition_CacheHitAfterAcquiringLock_ShouldReturnCachedResponse()
    {
        // Scenario: 
        // 1. GetAsync returned null (miss).
        // 2. TryAcquireLockAsync returned true (we got the lock).
        // 3. BUT, just before we got the lock, someone else finished saving a record (race condition).
        // 4. Double-check GetAsync should find it and return it instead of processing again.

        // Arrange
        var context = CreateContextWithIdempotency("test-race-condition");
        var cachedRecord = new IdempotencyRecord 
        { 
            StatusCode = 200, 
            Body = Encoding.UTF8.GetBytes("Cached Response"),
            Headers = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>()
        };

        // First GetAsync returns null (Miss)
        // Second GetAsync (inside lock) returns record (Hit)
        _mockStore.SetupSequence(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdempotencyRecord?)null) // First check
            .ReturnsAsync(cachedRecord);            // Double check

        _mockStore.Setup(s => s.TryAcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        await _middleware.InvokeAsync(context, _mockStore.Object);

        // Assert
        // Controller (_next) should NOT be called because we found it in the double-check
        _mockNext.Verify(n => n(It.IsAny<HttpContext>()), Times.Never);
        
        // Response should be what was in cache
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Equal("Cached Response", body);
        
        // Lock should still be released
        _mockStore.Verify(s => s.ReleaseLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_WhenKeyValidationFails_ShouldReturn400()
    {
        // Arrange
        // Validator accepts keys starting with "valid-"
        _options.KeyValidator = k => k.StartsWith("valid-");
        
        var context = CreateContextWithIdempotency("invalid-key");

        // Act
        await _middleware.InvokeAsync(context, _mockStore.Object);

        // Assert
        Assert.Equal(400, context.Response.StatusCode);
        
        // Store should NOT be accessed
        _mockStore.Verify(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockNext.Verify(n => n(It.IsAny<HttpContext>()), Times.Never);
    }

    private HttpContext CreateContextWithIdempotency(string key)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = key;
        context.Response.Body = new MemoryStream();

        // Add Endpoint Metadata
        var attribute = new IdempotentAttribute { ValidatePayload = false };
        var metadata = new EndpointMetadataCollection(attribute);
        var endpoint = new Endpoint(null, metadata, "Test Endpoint");
        
        var mockEndpointFeature = new Mock<IEndpointFeature>();
        mockEndpointFeature.Setup(f => f.Endpoint).Returns(endpoint);
        
        context.Features.Set<IEndpointFeature>(mockEndpointFeature.Object);

        return context;
    }
}
