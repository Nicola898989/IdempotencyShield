using IdempotencyShield.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace IdempotencyShield.TestApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaymentsController : ControllerBase
{
    private static int _requestCounter = 0;
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(ILogger<PaymentsController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Test endpoint with idempotency protection.
    /// </summary>
    [HttpPost]
    [Idempotent(ExpiryInMinutes = 60, ValidatePayload = true)]
    public async Task<IActionResult> ProcessPayment([FromBody] PaymentRequest request)
    {
        var requestNumber = Interlocked.Increment(ref _requestCounter);
        
        _logger.LogInformation(
            "Processing payment request #{RequestNumber} - Amount: {Amount}, UserId: {UserId}",
            requestNumber, request.Amount, request.UserId);

        // Simulate some processing time
        await Task.Delay(100);

        var result = new PaymentResponse
        {
            TransactionId = Guid.NewGuid().ToString(),
            Amount = request.Amount,
            Currency = request.Currency,
            Status = "Completed",
            ProcessedAt = DateTime.UtcNow,
            RequestNumber = requestNumber
        };

        return Ok(result);
    }

    /// <summary>
    /// Test endpoint WITHOUT idempotency protection for comparison.
    /// </summary>
    [HttpPost("without-protection")]
    public async Task<IActionResult> ProcessPaymentWithoutProtection([FromBody] PaymentRequest request)
    {
        var requestNumber = Interlocked.Increment(ref _requestCounter);
        
        _logger.LogInformation(
            "Processing payment WITHOUT protection #{RequestNumber} - Amount: {Amount}, UserId: {UserId}",
            requestNumber, request.Amount, request.UserId);

        await Task.Delay(100);

        var result = new PaymentResponse
        {
            TransactionId = Guid.NewGuid().ToString(),
            Amount = request.Amount,
            Currency = request.Currency,
            Status = "Completed",
            ProcessedAt = DateTime.UtcNow,
            RequestNumber = requestNumber
        };

        return Ok(result);
    }
}

public record PaymentRequest
{
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public string UserId { get; init; } = string.Empty;
}

public record PaymentResponse
{
    public string TransactionId { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public string Status { get; init; } = string.Empty;
    public DateTime ProcessedAt { get; init; }
    public int RequestNumber { get; init; }
}
