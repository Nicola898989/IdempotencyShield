using System.Net;
using System.Text;
using System.Text.Json;
using IdempotencyShield.Attributes;
using IdempotencyShield.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace IdempotencyShield.Tests;

/// <summary>
/// Controller di test per verificare il comportamento dell'idempotenza
/// </summary>
[ApiController]
[Route("api/test")]
public class TestController : ControllerBase
{
    private static int _executionCounter = 0;

    [HttpPost("payment")]
    [Idempotent(ExpiryInMinutes = 5, ValidatePayload = true)]
    public async Task<IActionResult> ProcessPayment([FromBody] PaymentRequest request)
    {
        // Incrementa il contatore atomicamente
        var currentExecution = Interlocked.Increment(ref _executionCounter);

        // Simula un'operazione che richiede tempo
        await Task.Delay(100);

        var response = new PaymentResponse
        {
            TransactionId = Guid.NewGuid().ToString(),
            ExecutionNumber = currentExecution,
            Timestamp = DateTime.UtcNow
        };

        return Ok(response);
    }

    public static void ResetCounter()
    {
        Interlocked.Exchange(ref _executionCounter, 0);
    }

    public static int GetExecutionCount()
    {
        return _executionCounter;
    }

    public record PaymentRequest
    {
        public decimal Amount { get; init; }
        public string UserId { get; init; } = string.Empty;
    }

    public record PaymentResponse
    {
        public string TransactionId { get; init; } = string.Empty;
        public int ExecutionNumber { get; init; }
        public DateTime Timestamp { get; init; }
    }
    [HttpPost("security-headers")]
    [Idempotent(ExpiryInMinutes = 5)]
    public IActionResult SecurityHeaders([FromBody] PaymentRequest request)
    {
        Response.Headers["Set-Cookie"] = "session_id=secret123; HttpOnly; Secure";
        Response.Headers["X-Custom-Header"] = "Allowed";
        
        return Ok(new { status = "secure" });
    }

    [HttpPost("exception")]
    [Idempotent(ExpiryInMinutes = 5)]
    public IActionResult ThrowException()
    {
        throw new InvalidOperationException("Simulated failure");
    }

    [HttpPost("empty-endpoint")]
    [Idempotent(ExpiryInMinutes = 5)]
    public IActionResult EmptyEndpoint()
    {
        return Ok();
    }
}

/// <summary>
/// Test per verificare il comportamento del middleware con richieste concorrenti
/// </summary>
public class ConcurrentRequestsTests
{
    private HttpClient CreateTestClient()
    {
        var builder = WebApplication.CreateBuilder();
        
        // Configura i servizi - Aggiungi l' assembly corrente per scoprire i controllers
        builder.Services.AddIdempotencyShield();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(TestController).Assembly);
        
        // Usa TestServer
        builder.WebHost.UseTestServer();

        var app = builder.Build();

        // Configura il middleware
        // Configura il middleware
        app.UseRouting();
        app.UseIdempotencyShield();
        app.MapControllers();

        app.StartAsync().GetAwaiter().GetResult();

        // Ritorna il client HTTP
        return app.GetTestServer().CreateClient();
    }

    [Fact]
    public async Task ConcurrentRequests_With10Requests_SameKey_ShouldExecuteControllerOnlyOnce()
    {
        // Arrange
        const int numberOfRequests = 10;
        const string idempotencyKey = "test-concurrent-key-001";
        
        var requestBody = new { amount = 100.00m, userId = "user123" };
        var json = JsonSerializer.Serialize(requestBody);

        TestController.ResetCounter();

        using var client = CreateTestClient();
        var tasks = new List<Task<HttpResponseMessage>>();

        // Act - Esegui 10 richieste concorrenti con la stessa chiave
        for (int i = 0; i < numberOfRequests; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Idempotency-Key", idempotencyKey);

            tasks.Add(client.SendAsync(request));
        }

        // Attendi il completamento di tutte le richieste
        var responses = await Task.WhenAll(tasks);

        // Assert
        var executionCount = TestController.GetExecutionCount();

        // ✅ VERIFICA PRINCIPALE: Il controller deve essere eseguito una sola volta
        Assert.Equal(1, executionCount);

        // Conta i diversi status code
        var successResponses = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var conflictResponses = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        // Almeno una deve avere successo (la prima)
        Assert.True(successResponses >= 1, $"Almeno una richiesta deve avere successo. Trovate: {successResponses}");
        
        // Le altre devono essere 409 Conflict o 200 OK (cache hit)
        Assert.True(successResponses + conflictResponses == numberOfRequests,
            $"Tutte le richieste devono essere 200 OK o 409 Conflict. " +
            $"OK: {successResponses}, Conflict: {conflictResponses}, Totale: {numberOfRequests}");

        // Verifica che tutte le risposte di successo abbiano lo stesso ExecutionNumber
        var successBodies = new List<TestController.PaymentResponse>();
        foreach (var response in responses.Where(r => r.StatusCode == HttpStatusCode.OK))
        {
            var body = await response.Content.ReadAsStringAsync();
            var paymentResponse = JsonSerializer.Deserialize<TestController.PaymentResponse>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            
            Assert.NotNull(paymentResponse);
            successBodies.Add(paymentResponse);
        }

        // Tutte le risposte di successo devono avere ExecutionNumber = 1
        Assert.All(successBodies, response => Assert.Equal(1, response.ExecutionNumber));

        // Verifica che tutte le risposte di successo abbiano lo stesso TransactionId (cached)
        var transactionIds = successBodies.Select(r => r.TransactionId).Distinct().ToList();
        Assert.Single(transactionIds);

        // Output di debug
        var output = $"""
            ===== Test: 10 Concurrent Requests =====
            Total Requests Sent: {numberOfRequests}
            Controller Executions: {executionCount} ✓
            Success Responses (200): {successResponses}
            Conflict Responses (409): {conflictResponses}
            Unique Transaction IDs: {transactionIds.Count}
            Transaction ID: {transactionIds.First()}
            ========================================
            """;
        
        Console.WriteLine(output);
    }

    [Fact]
    public async Task ConcurrentRequests_WithDifferentKeys_ShouldExecuteMultipleTimes()
    {
        // Arrange
        const int numberOfRequests = 5;
        var requestBody = new { amount = 100.00m, userId = "user123" };
        var json = JsonSerializer.Serialize(requestBody);

        TestController.ResetCounter();

        using var client = CreateTestClient();
        var tasks = new List<Task<HttpResponseMessage>>();

        // Act - Esegui 5 richieste concorrenti con chiavi DIVERSE
        for (int i = 0; i < numberOfRequests; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Idempotency-Key", $"different-key-{i}");

            tasks.Add(client.SendAsync(request));
        }

        var responses = await Task.WhenAll(tasks);

        // Assert
        var executionCount = TestController.GetExecutionCount();

        // Con chiavi diverse, tutte le richieste devono eseguire il controller
        Assert.True(executionCount >= numberOfRequests, 
            $"Controller deve essere eseguito almeno {numberOfRequests} volte. Esecuzioni: {executionCount}");

        // Tutte devono avere successo
        Assert.All(responses, response => 
            Assert.Equal(HttpStatusCode.OK, response.StatusCode));

        Console.WriteLine($"✓ Controller eseguito {executionCount} volte per {numberOfRequests} chiavi diverse");
    }

    [Fact]
    public async Task SequentialRequests_WithSameKey_ShouldReturnCachedResponse()
    {
        // Arrange
        const string idempotencyKey = "test-sequential-key-001";
        var requestBody = new { amount = 150.00m, userId = "user456" };
        var json = JsonSerializer.Serialize(requestBody);

        TestController.ResetCounter();

        using var client = CreateTestClient();

        // Act - Prima richiesta
        var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request1.Headers.Add("Idempotency-Key", idempotencyKey);
        var response1 = await client.SendAsync(request1);

        // Attendi che la prima richiesta sia completata
        await Task.Delay(200);
        
        // Seconda richiesta
        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/test/payment")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request2.Headers.Add("Idempotency-Key", idempotencyKey);
        var response2 = await client.SendAsync(request2);

        // Assert
        var executionCount = TestController.GetExecutionCount();

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        // Il controller deve essere eseguito solo una volta
        Assert.Equal(1, executionCount);

        // Le risposte devono essere identiche
        var body1 = await response1.Content.ReadAsStringAsync();
        var body2 = await response2.Content.ReadAsStringAsync();
        
        var payment1 = JsonSerializer.Deserialize<TestController.PaymentResponse>(body1, 
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var payment2 = JsonSerializer.Deserialize<TestController.PaymentResponse>(body2, 
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal(payment1!.TransactionId, payment2!.TransactionId);
        Assert.Equal(payment1.ExecutionNumber, payment2.ExecutionNumber);

        Console.WriteLine($"✓ Cached response verified: TransactionId={payment1.TransactionId}");
    }
}
