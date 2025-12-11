using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace IdempotencyShield.Tests.Compliance;

public abstract class IdempotencyStoreComplianceTests
{
    protected abstract HttpClient CreateClient();
    protected abstract Task CleanupAsync();

    [Fact]
    public async Task ConcurrentRequests_WithSameKey_ShouldExecuteOnlyOnce()
    {
        // cleanup before starting test
        await CleanupAsync();

        // Arrange
        const int concurrentRequests = 4;
        const string key = "compliance-concurrency-test";
        using var client = CreateClient();
        var json = JsonSerializer.Serialize(new { amount = 100m, userId = "compliance" });

        var tasks = new List<Task<HttpResponseMessage>>();

        // Act
        for (int i = 0; i < concurrentRequests; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Idempotency-Key", key);
            tasks.Add(client.SendAsync(request));
        }

        var responses = await Task.WhenAll(tasks);

        // Assert
        var successfulResponses = responses.Where(r => r.StatusCode == HttpStatusCode.OK).ToList();
        var conflictResponses = responses.Where(r => r.StatusCode == HttpStatusCode.Conflict).ToList();

        // One should succeed
        // One should succeed, others might be conflicts or replays
        Assert.NotEmpty(successfulResponses);
        
        var firstContent = await successfulResponses.First().Content.ReadAsStringAsync();
        var firstResult = JsonSerializer.Deserialize<TestController.PaymentResponse>(firstContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        Assert.NotNull(firstResult);
        Assert.True(firstResult.ExecutionNumber > 0);

        // Verify other successful responses are Replays of the SAME ExecutionNumber (Idempotency)
        foreach (var response in successfulResponses.Skip(1))
        {
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<TestController.PaymentResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.Equal(firstResult!.ExecutionNumber, result!.ExecutionNumber);
            Assert.Equal(firstResult.TransactionId, result.TransactionId);
        }
    }

    [Fact]
    public async Task SequentialRequests_WithSameKey_ShouldReturnCachedResponse()
    {
        await CleanupAsync();

        // Arrange
        const string key = "compliance-sequential-test";
        using var client = CreateClient();
        var json = JsonSerializer.Serialize(new { amount = 50m, userId = "seq" });

        // Act 1
        var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req1.Headers.Add("Idempotency-Key", key);
        var res1 = await client.SendAsync(req1);
        var body1 = await res1.Content.ReadAsStringAsync();
        var result1 = JsonSerializer.Deserialize<TestController.PaymentResponse>(body1, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Act 2
        var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req2.Headers.Add("Idempotency-Key", key);
        var res2 = await client.SendAsync(req2);
        var body2 = await res2.Content.ReadAsStringAsync();
        var result2 = JsonSerializer.Deserialize<TestController.PaymentResponse>(body2, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Assert
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        Assert.Equal(result1!.TransactionId, result2!.TransactionId);
        Assert.Equal(result1.ExecutionNumber, result2.ExecutionNumber);
    }
    
    [Fact]
    public async Task Security_KeyWithSqlInjection_ShouldBeHandledSafely()
    {
        await CleanupAsync();

        // Arrange
        // Classic SQL injection attempt
        const string maliciousKey = "test-key' OR '1'='1";
        using var client = CreateClient();
        var json = JsonSerializer.Serialize(new { amount = 50m, userId = "hacker" });

        // Act 1
        var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req1.Headers.Add("Idempotency-Key", maliciousKey);
        var res1 = await client.SendAsync(req1);

        // Act 2
        var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req2.Headers.Add("Idempotency-Key", maliciousKey);
        var res2 = await client.SendAsync(req2);

        // Assert
        // Should not crash, should treat as a literal key
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        
        var body1 = await res1.Content.ReadAsStringAsync();
        var body2 = await res2.Content.ReadAsStringAsync();
        
        var result1 = JsonSerializer.Deserialize<TestController.PaymentResponse>(body1, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var result2 = JsonSerializer.Deserialize<TestController.PaymentResponse>(body2, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal(result1.TransactionId, result2.TransactionId);
    }

    [Fact]
    public async Task Security_KeyWithXssPayload_ShouldBeHandledSafely()
    {
        await CleanupAsync();

        // Arrange
        const string maliciousKey = "<script>alert('xss')</script>";
        using var client = CreateClient();
        var json = JsonSerializer.Serialize(new { amount = 50m, userId = "script-kiddie" });

        // Act
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Idempotency-Key", maliciousKey);
        var res = await client.SendAsync(req);

        // Assert
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        // The key should be stored as-is, but the system shouldn't execute it. 
        // We verify it didn't crash 500.
    }
}
