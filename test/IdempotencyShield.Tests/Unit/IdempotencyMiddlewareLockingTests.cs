using System.Threading.Tasks;
using IdempotencyShield.Attributes;
using IdempotencyShield.Configuration;
using IdempotencyShield.Exceptions;
using IdempotencyShield.Middleware;
using IdempotencyShield.Models;
using IdempotencyShield.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace IdempotencyShield.Tests.Unit;

public class IdempotencyMiddlewareLockingTests
{
    private readonly IIdempotencyStore _mockStore;
    private readonly RequestDelegate _mockNext;
    private readonly IdempotencyOptions _options;
    private readonly IdempotencyMiddleware _middleware;
    private readonly IOptions<IdempotencyOptions> _optionsWrapper;

    public IdempotencyMiddlewareLockingTests()
    {
        _mockStore = Substitute.For<IIdempotencyStore>();
        _mockNext = Substitute.For<RequestDelegate>();
        _options = new IdempotencyOptions();

        _optionsWrapper = Substitute.For<IOptions<IdempotencyOptions>>();
        _optionsWrapper.Value.Returns(_options);

        _middleware = new IdempotencyMiddleware(_mockNext, _optionsWrapper);
    }

    [Fact]
    public async Task InvokeAsync_WhenLockTimeoutIsZero_AndLockIsTaken_ShouldReturn409()
    {
        // Arrange
        _options.LockTimeoutMilliseconds = 0;
        var context = CreateContextWithIdempotency("test-key");

        _mockStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IdempotencyRecord?>(null));
        _mockStore.TryAcquireLockAsync("test-key", 0, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        // Act
        await _middleware.InvokeAsync(context, _mockStore);

        // Assert
        Assert.Equal(409, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WhenLockTimeoutIsPositive_AndLockIsTaken_ShouldThrowLockTimeoutException()
    {
        // Arrange
        _options.LockTimeoutMilliseconds = 100;
        var context = CreateContextWithIdempotency("test-key");

        _mockStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IdempotencyRecord?>(null));
        _mockStore.TryAcquireLockAsync("test-key", 100, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<LockTimeoutException>(() =>
            _middleware.InvokeAsync(context, _mockStore));

        Assert.Equal("test-key", exception.IdempotencyKey);
        Assert.Equal(100, exception.LockTimeoutMilliseconds);
    }

    private HttpContext CreateContextWithIdempotency(string key)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = key;
        context.Response.Body = new System.IO.MemoryStream();

        var attribute = new IdempotentAttribute();
        var metadata = new EndpointMetadataCollection(attribute);
        var endpoint = new Endpoint(null, metadata, "Test Endpoint");

        var endpointFeature = Substitute.For<IEndpointFeature>();
        endpointFeature.Endpoint.Returns(endpoint);

        context.Features.Set<IEndpointFeature>(endpointFeature);

        return context;
    }
}
