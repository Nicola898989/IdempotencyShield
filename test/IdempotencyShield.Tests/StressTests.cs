using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using IdempotencyShield.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace IdempotencyShield.Tests;

public class StressTests
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
    public async Task Request_WhenCancelledByClient_ShouldReleaseLock()
    {
        // Arrange
        const string key = "test-cancellation";
        using var client = CreateTestClient();
        
        // Act 1: Send request and cancel it immediately/shortly
        var cts = new CancellationTokenSource();
        var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { amount = 100m, userId = "cancel" }), Encoding.UTF8, "application/json")
        };
        request1.Headers.Add("Idempotency-Key", key);

        // Cancel after 50ms (controller takes 100ms)
        cts.CancelAfter(50);
        
        try
        {
            await client.SendAsync(request1, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (Exception)
        {
            // Ignore other exceptions
        }

        // Wait a bit to ensure server processed the cancellation/cleanup
        await Task.Delay(200);

        // Act 2: Retry - should succeed if lock was released
        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { amount = 100m, userId = "cancel" }), Encoding.UTF8, "application/json")
        };
        request2.Headers.Add("Idempotency-Key", key);

        var response2 = await client.SendAsync(request2);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
    }

    [Fact]
    public async Task Request_WithVeryLongKey_ShouldWork()
    {
        // Arrange
        // Create a key of 2KB
        var key = new string('a', 2048);
        using var client = CreateTestClient();
        
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { amount = 100m, userId = "longkey" }), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        
        // Replay
        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { amount = 100m, userId = "longkey" }), Encoding.UTF8, "application/json")
        };
        request2.Headers.Add("Idempotency-Key", key);
        var response2 = await client.SendAsync(request2);
        
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
    }

    [Fact]
    public async Task RapidFire_Requests_ShouldNotCauseDeadlock()
    {
        // Arrange
        const int iterations = 50;
        using var client = CreateTestClient();
        var json = JsonSerializer.Serialize(new { amount = 10m, userId = "rapid" });
        
        var tasks = new List<Task<HttpResponseMessage>>();

        // Act: Fire 50 requests with DIFFERENT keys rapidly
        for (int i = 0; i < iterations; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Idempotency-Key", $"rapid-key-{i}");
            tasks.Add(client.SendAsync(request));
        }

        // Act 2: Fire 50 requests with SAME key rapidly
        const string sameKey = "rapid-same-key";
        for (int i = 0; i < iterations; i++)
        {
             var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Idempotency-Key", sameKey);
            tasks.Add(client.SendAsync(request));
        }

        var responses = await Task.WhenAll(tasks);

        // Assert
        var successCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var conflictCount = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);
        
        // Different keys should all be OK (50)
        // Same keys: 1 OK + 49 Conflicts (or OK if cached quickly)
        // Total OK >= 51
        
        Assert.True(successCount >= 51, $"Expected at least 51 successes. Got {successCount}");
        // Deadlocks would cause timeouts/Failures, so completion implies no deadlock capable of hanging the test
    }
}
