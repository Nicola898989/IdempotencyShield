namespace IdempotencyShield.Configuration;

/// <summary>
/// Configuration options for the IdempotencyShield middleware.
/// </summary>
public class IdempotencyOptions
{
    /// <summary>
    /// The name of the HTTP header that contains the idempotency key.
    /// Default is "Idempotency-Key".
    /// </summary>
    public string HeaderName { get; set; } = "Idempotency-Key";

    /// <summary>
    /// Default expiry time in minutes for cached responses when not specified by the attribute.
    /// Default is 60 minutes.
    /// </summary>
    public int DefaultExpiryMinutes { get; set; } = 60;

    /// <summary>
    /// Maximum time in milliseconds to wait for lock acquisition before returning 409 Conflict.
    /// Set to 0 for immediate failure if lock is held.
    /// Default is 0 (no waiting).
    /// </summary>
    public int LockTimeoutMilliseconds { get; set; } = 0;
    /// <summary>
    /// Optional validator function for the Idempotency-Key.
    /// If provided, this function will be called with the key value.
    /// If it returns false, the middleware will return 400 Bad Request.
    /// </summary>
    public Func<string, bool>? KeyValidator { get; set; }

    /// <summary>
    /// Maximum request body size in bytes to allow for payload validation hashing.
    /// Helps prevent Denial-of-Service attacks by limiting memory usage.
    /// Default is 10 MB.
    /// </summary>
    public long MaxRequestBodySize { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// A set of response headers that should not be cached.
    /// Header names are case-insensitive.
    /// </summary>
    public ISet<string> ExcludedHeaders { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Transfer-Encoding",
        "Connection",
        "Keep-Alive",
        "Upgrade",
        "Date",
        "Set-Cookie",
        "Authorization"
    };
}
