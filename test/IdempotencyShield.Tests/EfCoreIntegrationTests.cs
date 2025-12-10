using IdempotencyShield.EntityFrameworkCore;
using IdempotencyShield.EntityFrameworkCore.Entities;
using IdempotencyShield.EntityFrameworkCore.Extensions;
using IdempotencyShield.Extensions; // Required for UseIdempotencyShield
using IdempotencyShield.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Text.Json;
using System.Text;
using Xunit;

namespace IdempotencyShield.Tests;



public class EfCoreIntegrationTests
{
    private HttpClient CreateTestClient()
    {
        var builder = WebApplication.CreateBuilder();

        // Setup EF Core Sqlite (In-Memory)
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("Filename=:memory:");
        connection.Open(); // Keep connection open

        builder.Services.AddDbContext<TestIdempotencyContext>(options =>
            options.UseSqlite(connection));
        
        // Register connection as singleton to prevent disposal? 
        // Actually, we just need it available during CreateTestClient scope until app starts?
        // No, for DI lifetime, we should register it or handle it. 
        // But simpler: just use generic builder lambda.
        // We need to EnsureCreated.

        // Register IdempotencyShield with EF Core
        builder.Services.AddIdempotencyShieldWithEfCore<TestIdempotencyContext>();

        builder.Services.AddControllers()
            .AddApplicationPart(typeof(TestController).Assembly);

        builder.WebHost.UseTestServer();

        var app = builder.Build();

        // Ensure DB is created
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestIdempotencyContext>();
            db.Database.EnsureCreated();
        }

        app.UseRouting();
        app.UseIdempotencyShield();
        app.MapControllers();

        app.StartAsync().GetAwaiter().GetResult();

        return app.GetTestServer().CreateClient();
    }

    [Fact]
    public async Task EFCore_Store_ShouldCacheAndReplayResponse()
    {
        // Arrange
        const string key = "ef-core-cache-test";
        using var client = CreateTestClient();
        var json = JsonSerializer.Serialize(new { amount = 100m, userId = "efcore" });

        // Act 1: First Request
        var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request1.Headers.Add("Idempotency-Key", key);
        var response1 = await client.SendAsync(request1);
        
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        var body1 = await response1.Content.ReadAsStringAsync();
        var result1 = JsonSerializer.Deserialize<TestController.PaymentResponse>(body1, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Act 2: Second Request (Should be cached)
        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request2.Headers.Add("Idempotency-Key", key);
        var response2 = await client.SendAsync(request2);

        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        var body2 = await response2.Content.ReadAsStringAsync();
        var result2 = JsonSerializer.Deserialize<TestController.PaymentResponse>(body2, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal(result1.TransactionId, result2.TransactionId); // Same ID = Cached
        Assert.Equal(result1.ExecutionNumber, result2.ExecutionNumber);
    }
}
