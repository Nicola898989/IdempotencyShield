using System;

namespace IdempotencyShield.Exceptions;

/// <summary>
/// Exception thrown when a request body exceeds the configured maximum size for idempotency payload validation.
/// </summary>
public class RequestBodyTooLargeException : IdempotencyShieldException
{
    /// <summary>
    /// The size of the request body in bytes.
    /// </summary>
    public long RequestBodySize { get; }

    /// <summary>
    /// The configured maximum allowed request body size in bytes.
    /// </summary>
    public long MaxRequestBodySize { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestBodyTooLargeException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="requestBodySize">The size of the request body.</param>
    /// <param name="maxRequestBodySize">The configured maximum size.</param>
    public RequestBodyTooLargeException(string message, long requestBodySize, long maxRequestBodySize) : base(message)
    {
        RequestBodySize = requestBodySize;
        MaxRequestBodySize = maxRequestBodySize;
    }
}
