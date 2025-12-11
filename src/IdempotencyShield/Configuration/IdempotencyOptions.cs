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
    /// The maximum time in milliseconds that the lock remains valid (TTL) to prevent deadlocks.
    /// Default is 30000 ms (30 seconds).
    /// </summary>
    public int LockExpirationMilliseconds { get; set; } = 30000;

    /// <summary>
    /// Maximum time in milliseconds to wait for lock acquisition before returning 409 Conflict.
    /// Default is 0 (no waiting, fail-fast).
    /// </summary>
    public int LockWaitTimeoutMilliseconds { get; set; } = 0;
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

    /// <summary>
    /// The failure mode to use when the idempotency store is unavailable (e.g., Redis down).
    /// Default is FailSafe (throws 500).
    /// </summary>
    public IdempotencyFailureMode FailureMode { get; set; } = IdempotencyFailureMode.FailSafe;

    /// <summary>
    /// The number of retry attempts for transient storage failures.
    /// Default is 0 (no retries).
    /// </summary>
    public int StorageRetryCount { get; set; } = 0;

    /// <summary>
    /// The delay in milliseconds between retry attempts.
    /// Default is 200 milliseconds.
    /// </summary>
    public int StorageRetryDelayMilliseconds { get; set; } = 200;
}

/// <summary>
/// Defines the behavior when the idempotency store is unavailable.
/// </summary>
public enum IdempotencyFailureMode
{
    /// <summary>
    /// Prevents the request from being processed if the store is unavailable.
    /// Ensures idempotency but reduces availability.
    /// </summary>
    FailSafe,

    /// <summary>
    /// Proceeds with request processing (skipping idempotency checks) if the store is unavailable.
    /// Increases availability but may allow duplicate requests.
    /// </summary>
    FailOpen
}
