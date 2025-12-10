using System.Net;
using System.Text;
using System.Text.Json;
using IdempotencyShield.Extensions;
using IdempotencyShield.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace IdempotencyShield.Tests;

public class EdgeCaseTests
{
    private HttpClient CreateTestClient(Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        
        builder.Services.AddIdempotencyShield();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(TestController).Assembly);

        configureServices?.Invoke(builder.Services);
        
        builder.WebHost.UseTestServer();

        var app = builder.Build();

        app.UseRouting();
        app.UseIdempotencyShield();
        app.MapControllers();

        app.StartAsync().GetAwaiter().GetResult();

        return app.GetTestServer().CreateClient();
    }

    [Fact]
    public async Task Request_WithoutIdempotencyKey_ShouldPassThroughAndExecute()
    {
        // Arrange
        TestController.ResetCounter();
        using var client = CreateTestClient();
        var requestBody = new { amount = 100.00m, userId = "user_no_key" };
        var json = JsonSerializer.Serialize(requestBody);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        // No Idempotency-Key header added

        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, TestController.GetExecutionCount());
        
        // Send again - should execute again (no idempotency)
        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        await client.SendAsync(request2);
        Assert.Equal(2, TestController.GetExecutionCount());
    }

    [Fact]
    public async Task Request_WithDifferentPayload_SameKey_ShouldReturn422()
    {
        // Arrange
        const string key = "test-payload-mismatch";
        TestController.ResetCounter();
        using var client = CreateTestClient();

        // First Request
        var json1 = JsonSerializer.Serialize(new { amount = 100.00m, userId = "user1" });
        var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(json1, Encoding.UTF8, "application/json")
        };
        request1.Headers.Add("Idempotency-Key", key);
        
        await client.SendAsync(request1);

        // Act - Second Request with SAME key but DIFFERENT payload
        var json2 = JsonSerializer.Serialize(new { amount = 200.00m, userId = "user1" });
        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(json2, Encoding.UTF8, "application/json")
        };
        request2.Headers.Add("Idempotency-Key", key);

        var response2 = await client.SendAsync(request2);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response2.StatusCode); // 422
        var error = await response2.Content.ReadAsStringAsync();
        Assert.Contains("different request payload", error);
        
        // Controller should have executed only once
        Assert.Equal(1, TestController.GetExecutionCount());
    }

    [Fact]
    public async Task Request_WithLargePayload_ShouldFail()
    {
        // Arrange
        const string key = "test-large-payload";
        using var client = CreateTestClient();
        
        // Create a payload > 10MB
        var largeString = new string('A', 10 * 1024 * 1024 + 100); 
        var requestBody = new { data = largeString };
        var json = JsonSerializer.Serialize(requestBody);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RequestBodyTooLargeException>(async () => 
        {
            // Note: TestServer might wrap exceptions or return 500. 
            // If it returns 500, we check status code. If it bubbles up, we check exception.
            // In-process TestServer usually bubbles up exceptions if not handled.
            try 
            {
                var response = await client.SendAsync(request);
                if (response.StatusCode == HttpStatusCode.InternalServerError)
                {
                    // If caught by developer exception page or similar
                    throw new InvalidOperationException("Wrapped error");
                }
            }
            catch (Exception ex) when (ex.Message.Contains("too large") || (ex.InnerException?.Message.Contains("too large") ?? false))
            {
                throw new InvalidOperationException("Request body too large", ex);
            }
        });
        
        Assert.Contains("exceeds the configured limit", exception.Message);
    }

    [Fact]
    public async Task Response_ShouldNotCache_SecurityHeaders()
    {
        // Arrange
        const string key = "test-security-headers";
        using var client = CreateTestClient();
        var json = JsonSerializer.Serialize(new { amount = 100.00m, userId = "user_sec" });

        // Act 1: First Request (Generates response with Set-Cookie)
        var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/test/security-headers")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request1.Headers.Add("Idempotency-Key", key);
        
        var response1 = await client.SendAsync(request1);
        
        // Assert 1: First response SHOULD have the cookie (it's fresh)
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.True(response1.Headers.Contains("Set-Cookie") || response1.Content.Headers.Contains("Set-Cookie") || response1.Headers.TryGetValues("Set-Cookie", out _), 
            "First response should contain Set-Cookie");

        // Act 2: Second Request (Cached)
        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/test/security-headers")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request2.Headers.Add("Idempotency-Key", key);
        
        var response2 = await client.SendAsync(request2);

        // Assert 2: Cached response should NOT have the cookie
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.False(response2.Headers.Contains("Set-Cookie"), "Cached response should NOT contain Set-Cookie header");
        
        // But should have other headers
        Assert.True(response2.Headers.Contains("X-Custom-Header"), "Cached response SHOULD contain safe headers");
    }

    [Fact]
    public async Task Request_WhenControllerThrows_ShouldReleaseLock_AndAllowRetry()
    {
        // Arrange
        const string key = "test-exception-retry";
        using var client = CreateTestClient();
        
        // Act 1: Request that throws
        var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/test/exception");
        request1.Headers.Add("Idempotency-Key", key);
        
        try 
        {
            await client.SendAsync(request1);
        }
        catch
        {
            // Ignore exception from test server
        }

        // Act 2: Retry with same key (should NOT be locked)
        // We use a different endpoint to verify we can acquire lock again (or same endpoint if we want to fail again, but let's use a working one to verify success)
        // Actually, the key is bound to the response. If the first failed, nothing should be cached.
        // So if we call a working endpoint with SAME key, it should work (if we didn't cache the failure).
        // But wait, the key is usually bound to the request payload hash too if validation is on.
        // The exception endpoint doesn't validate payload (default false? No, attribute default is false? Let's check).
        // [Idempotent(ExpiryInMinutes = 5)] -> ValidatePayload is false by default.
        
        // So we can reuse the key for a different endpoint? No, the key is global for the store.
        // But if we call the SAME endpoint again, it should retry.
        
        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/test/exception");
        request2.Headers.Add("Idempotency-Key", key);
        
        // This should run again (and throw again), NOT return 409 Conflict.
        // If it returns 409, it means lock wasn't released.
        
        HttpResponseMessage? response2 = null;
        try 
        {
            response2 = await client.SendAsync(request2);
        }
        catch
        {
            // Expected to throw again
        }
        
        // If we got here and didn't hang or get 409 (if we could see it), we are good.
        // Better test: Call a working endpoint with same key?
        // If we call /api/test/payment with same key, it should work (since previous failed and didn't cache).
        
        var json = JsonSerializer.Serialize(new { amount = 100.00m, userId = "retry" });
        var request3 = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request3.Headers.Add("Idempotency-Key", key);
        
        var response3 = await client.SendAsync(request3);
        
        Assert.Equal(HttpStatusCode.OK, response3.StatusCode);
    }

    [Fact]
    public async Task Request_WithSpecialCharactersInKey_ShouldWork()
    {
        // Arrange
        const string key = "test/key@with#special$chars%&";
        using var client = CreateTestClient();
        var json = JsonSerializer.Serialize(new { amount = 100.00m, userId = "special" });

        // Act
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        
        // Replay
        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request2.Headers.Add("Idempotency-Key", key);
        var response2 = await client.SendAsync(request2);
        
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
    }

    [Fact]
    public async Task Request_WithEmptyBody_ShouldWork()
    {
        // Arrange
        const string key = "test-empty-body";
        using var client = CreateTestClient();
        
        // We need an endpoint that accepts empty body or doesn't validate it.
        // Payment endpoint expects body.
        // We added EmptyEndpoint to TestController.
        
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/empty-endpoint");
        request.Headers.Add("Idempotency-Key", key);
        
        var response = await client.SendAsync(request);
        
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
