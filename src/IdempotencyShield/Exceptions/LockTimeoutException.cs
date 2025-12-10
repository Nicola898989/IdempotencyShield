using System;

namespace IdempotencyShield.Exceptions;

/// <summary>
/// Exception thrown when a distributed lock cannot be acquired within the configured timeout.
/// </summary>
public class LockTimeoutException : IdempotencyShieldException
{
    /// <summary>
    /// The idempotency key for which the lock timed out.
    /// </summary>
    public string IdempotencyKey { get; }

    /// <summary>
    /// The configured lock acquisition timeout in milliseconds.
    /// </summary>
    public int LockTimeoutMilliseconds { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="LockTimeoutException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="idempotencyKey">The idempotency key.</param>
    /// <param name="lockTimeoutMilliseconds">The configured timeout.</param>
    public LockTimeoutException(string message, string idempotencyKey, int lockTimeoutMilliseconds) : base(message)
    {
        IdempotencyKey = idempotencyKey;
        LockTimeoutMilliseconds = lockTimeoutMilliseconds;
    }
}
