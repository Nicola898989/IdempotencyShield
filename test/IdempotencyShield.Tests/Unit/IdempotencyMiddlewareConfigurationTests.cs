using System.Text;
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

public class IdempotencyMiddlewareConfigurationTests
{
    private readonly IIdempotencyStore _mockStore;
    private readonly RequestDelegate _mockNext;
    private readonly IdempotencyOptions _options;
    private readonly IdempotencyMiddleware _middleware;
    private readonly IOptions<IdempotencyOptions> _optionsWrapper;

    public IdempotencyMiddlewareConfigurationTests()
    {
        _mockStore = Substitute.For<IIdempotencyStore>();
        _mockNext = Substitute.For<RequestDelegate>();
        _options = new IdempotencyOptions();

        _optionsWrapper = Substitute.For<IOptions<IdempotencyOptions>>();
        _optionsWrapper.Value.Returns(_options);

        _middleware = new IdempotencyMiddleware(_mockNext, _optionsWrapper);
    }

    [Fact]
    public async Task InvokeAsync_WhenRequestBodyExceedsLimit_ShouldThrowRequestBodyTooLargeException()
    {
        // Arrange
        _options.MaxRequestBodySize = 10; // 10 bytes limit
        var context = CreateContextWithIdempotency("test-key", new byte[11]); // 11 bytes body

        _mockStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IdempotencyRecord?>(null));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RequestBodyTooLargeException>(() =>
            _middleware.InvokeAsync(context, _mockStore));

        Assert.Equal(11, exception.RequestBodySize);
        Assert.Equal(10, exception.MaxRequestBodySize);
    }

    [Fact]
    public async Task InvokeAsync_WhenHeaderIsExcluded_ShouldNotBeCached()
    {
        // Arrange
        _options.ExcludedHeaders.Add("X-Test-Header");
        var context = CreateContextWithIdempotency("test-key-headers", new byte[10]);

        _mockStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IdempotencyRecord?>(null));
        _mockStore.TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        _mockNext.Invoke(Arg.Any<HttpContext>()).Returns(Task.CompletedTask)
            .AndDoes(ctx =>
            {
                ctx.Arg<HttpContext>().Response.StatusCode = 200;
                ctx.Arg<HttpContext>().Response.Headers["X-Test-Header"] = "should-be-excluded";
                ctx.Arg<HttpContext>().Response.Headers["X-Another-Header"] = "should-be-included";
            });

        // Act
        await _middleware.InvokeAsync(context, _mockStore);

        // Assert
        await _mockStore.Received(1).SaveAsync(
            Arg.Any<string>(),
            Arg.Is<IdempotencyRecord>(r =>
                !r.Headers.ContainsKey("X-Test-Header") &&
                r.Headers.ContainsKey("X-Another-Header")),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    private HttpContext CreateContextWithIdempotency(string key, byte[]? body = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = key;
        context.Response.Body = new MemoryStream();

        if (body != null)
        {
            context.Request.Body = new MemoryStream(body);
            context.Request.ContentLength = body.Length;
        }

        // Add Endpoint Metadata
        var attribute = new IdempotentAttribute { ValidatePayload = true };
        var metadata = new EndpointMetadataCollection(attribute);
        var endpoint = new Endpoint(null, metadata, "Test Endpoint");

        var endpointFeature = Substitute.For<IEndpointFeature>();
        endpointFeature.Endpoint.Returns(endpoint);

        context.Features.Set<IEndpointFeature>(endpointFeature);

        return context;
    }
}
