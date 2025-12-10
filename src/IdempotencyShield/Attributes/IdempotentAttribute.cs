namespace IdempotencyShield.Attributes;

/// <summary>
/// Marks a controller or action as idempotent, enabling automatic request deduplication and response caching.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public class IdempotentAttribute : Attribute
{
    /// <summary>
    /// How long (in minutes) the cached response should be retained.
    /// Default is 60 minutes.
    /// </summary>
    public int ExpiryInMinutes { get; set; } = 60;

    /// <summary>
    /// Whether to validate that the request body matches the original request.
    /// If true, requests with the same idempotency key but different bodies will return 422 Unprocessable Entity.
    /// Default is true.
    /// </summary>
    public bool ValidatePayload { get; set; } = true;
}
