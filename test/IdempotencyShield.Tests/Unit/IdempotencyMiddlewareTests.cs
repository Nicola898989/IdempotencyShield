using System.Text;
using IdempotencyShield.Attributes;
using IdempotencyShield.Configuration;
using IdempotencyShield.Middleware;
using IdempotencyShield.Models;
using IdempotencyShield.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace IdempotencyShield.Tests.Unit;

public class IdempotencyMiddlewareTests
{
    private readonly IIdempotencyStore _mockStore;
    private readonly RequestDelegate _mockNext;
    private readonly IdempotencyOptions _options;
    private readonly IdempotencyMiddleware _middleware;
    private readonly IOptions<IdempotencyOptions> _optionsWrapper;

    public IdempotencyMiddlewareTests()
    {
        _mockStore = Substitute.For<IIdempotencyStore>();
        _mockNext = Substitute.For<RequestDelegate>();
        _options = new IdempotencyOptions { HeaderName = "Idempotency-Key" };
        
        _optionsWrapper = Substitute.For<IOptions<IdempotencyOptions>>();
        _optionsWrapper.Value.Returns(_options);

        _middleware = new IdempotencyMiddleware(_mockNext, _optionsWrapper);
    }

    [Fact]
    public async Task InvokeAsync_WhenControllerReturnsError_ShouldNotSaveToStore()
    {
        // Arrange
        var context = CreateContextWithIdempotency("test-key-error");
        
        // Mock store: No existing key, lock acquired successfully
        _mockStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IdempotencyRecord?>(null));
        
        _mockStore.TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Mock Next: Controller throws or returns 500
        _mockNext.Invoke(Arg.Any<HttpContext>()).Returns(Task.CompletedTask)
            .AndDoes(ctx => ctx.Arg<HttpContext>().Response.StatusCode = 500);

        // Act
        await _middleware.InvokeAsync(context, _mockStore);

        // Assert
        // We expect SaveAsync to NEVER be called because status is 500
        await _mockStore.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default, default);
        
        // Lock should still be released
        await _mockStore.Received(1).ReleaseLockAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        _mockStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IdempotencyRecord?>(null), Task.FromResult<IdempotencyRecord?>(cachedRecord));

        _mockStore.TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Act
        await _middleware.InvokeAsync(context, _mockStore);

        // Assert
        // Controller (_next) should NOT be called because we found it in the double-check
        await _mockNext.DidNotReceive().Invoke(Arg.Any<HttpContext>());
        
        // Response should be what was in cache
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Equal("Cached Response", body);
        
        // Lock should still be released
        await _mockStore.Received(1).ReleaseLockAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_WhenKeyValidationFails_ShouldReturn400()
    {
        // Arrange
        // Validator accepts keys starting with "valid-"
        _options.KeyValidator = k => k.StartsWith("valid-");
        
        var context = CreateContextWithIdempotency("invalid-key");

        // Act
        await _middleware.InvokeAsync(context, _mockStore);

        // Assert
        Assert.Equal(400, context.Response.StatusCode);
        
        // Store should NOT be accessed
        await _mockStore.DidNotReceiveWithAnyArgs().GetAsync(default!, default);
        await _mockNext.DidNotReceive().Invoke(Arg.Any<HttpContext>());
    }

    [Fact]
    public async Task InvokeAsync_FailSafe_WhenStoreThrows_ShouldThrow()
    {
        // Arrange
        _options.FailureMode = IdempotencyFailureMode.FailSafe;
        var context = CreateContextWithIdempotency("failsafe-test");

        _mockStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IdempotencyRecord?>(new Exception("Redis Down")));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _middleware.InvokeAsync(context, _mockStore));
    }

    [Fact]
    public async Task InvokeAsync_FailOpen_WhenStoreThrows_ShouldProceed()
    {
        // Arrange
        _options.FailureMode = IdempotencyFailureMode.FailOpen;
        var context = CreateContextWithIdempotency("failopen-test");

        // Fail on GetAsync
        _mockStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IdempotencyRecord?>(new Exception("Redis Down")));

        // Fail on TryAcquireLockAsync
        _mockStore.TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromException<bool>(new Exception("Redis Down")));

        // Mock Next to succeed
        _mockNext.Invoke(Arg.Any<HttpContext>()).Returns(Task.CompletedTask);

        // Act
        await _middleware.InvokeAsync(context, _mockStore);

        // Assert
        // Should have called _next
        await _mockNext.Received(1).Invoke(Arg.Any<HttpContext>());
        
        // Assert response status is 200 (default)
        Assert.Equal(200, context.Response.StatusCode);
    }
    
    [Fact]
    public async Task InvokeAsync_RetryLogic_ShouldRetryOnFailure()
    {
        // Arrange
        _options.StorageRetryCount = 2;
        _options.StorageRetryDelayMilliseconds = 1; // Fast test
        var context = CreateContextWithIdempotency("retry-test");

        // Fail first 2 times, succeed on 3rd
        _mockStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<IdempotencyRecord?>(new Exception("Fail 1")),
                Task.FromException<IdempotencyRecord?>(new Exception("Fail 2")),
                Task.FromResult<IdempotencyRecord?>(null) // Success (Miss)
            );

        _mockStore.TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Act
        await _middleware.InvokeAsync(context, _mockStore);

        // Assert
        // GetAsync should have been called 4 times:
        // 1. Initial GetAsync (Fail 1)
        // 2. Retry GetAsync (Fail 2)
        // 3. Retry GetAsync (Success - Miss) -> proceed to lock
        // 4. Double-check GetAsync inside lock (Success - Miss)
        await _mockStore.Received(4).GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_FailOpen_WhenSaveThrows_ShouldSwallowAndReturnResponse()
    {
         // Arrange
        _options.FailureMode = IdempotencyFailureMode.FailOpen;
        var context = CreateContextWithIdempotency("failopen-save-test");
        context.Response.StatusCode = 200; // Original response

        // Success on Get and Lock
        _mockStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IdempotencyRecord?>(null)); // Miss
        
        _mockStore.TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Mock SaveAsync to throw
        _mockStore.SaveAsync(Arg.Any<string>(), Arg.Any<IdempotencyRecord>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Exception("Redis Down on Save")));

        _mockNext.Invoke(Arg.Any<HttpContext>()).Returns(Task.CompletedTask);

        // Act
        // Should NOT throw
        await _middleware.InvokeAsync(context, _mockStore);

        // Assert
        // Verified we reached SaveAsync
        await _mockStore.Received(1).SaveAsync(Arg.Any<string>(), Arg.Any<IdempotencyRecord>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Response should still be valid
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_FailSafe_WhenRetryExhausted_ShouldThrow()
    {
         // Arrange
        _options.FailureMode = IdempotencyFailureMode.FailSafe;
        _options.StorageRetryCount = 2; // Retry 2 times
        _options.StorageRetryDelayMilliseconds = 1;
        var context = CreateContextWithIdempotency("failsafe-exhausted-test");

        // Always fail
        _mockStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IdempotencyRecord?>(new Exception("Persistent Failure")));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _middleware.InvokeAsync(context, _mockStore));
        
        // Assert called 3 times (1 initial + 2 retries)
        await _mockStore.Received(3).GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        
        var endpointFeature = Substitute.For<IEndpointFeature>();
        endpointFeature.Endpoint.Returns(endpoint);
        
        context.Features.Set<IEndpointFeature>(endpointFeature);

        return context;
    }
}
