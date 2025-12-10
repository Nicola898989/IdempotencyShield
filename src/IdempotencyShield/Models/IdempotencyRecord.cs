using Microsoft.Extensions.Primitives;

namespace IdempotencyShield.Models;

/// <summary>
/// Represents a cached idempotent response with all necessary data to replay the response.
/// </summary>
public class IdempotencyRecord
{
    /// <summary>
    /// The HTTP status code of the cached response.
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// The response headers to be replayed.
    /// </summary>
    public Dictionary<string, StringValues> Headers { get; set; } = new();

    /// <summary>
    /// The response body as a byte array.
    /// </summary>
    public byte[] Body { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Timestamp when this record was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// SHA256 hash of the original request body for payload validation.
    /// Ensures the same idempotency key is not reused for different operations.
    /// </summary>
    public string? RequestBodyHash { get; set; }
}
