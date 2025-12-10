using System.Security.Cryptography;
using System.Text;
using IdempotencyShield.Attributes;
using IdempotencyShield.Configuration;
using IdempotencyShield.Models;
using IdempotencyShield.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace IdempotencyShield.Middleware;

/// <summary>
/// Middleware that intercepts HTTP requests decorated with [Idempotent] attribute
/// and ensures they are processed only once, caching and replaying responses for duplicate requests.
/// </summary>
public class IdempotencyMiddleware
{
    private readonly RequestDelegate _next;
    // Store is now injected in InvokeAsync to support Scoped lifetime
    private readonly IdempotencyOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdempotencyMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="options">Configuration options.</param>
    public IdempotencyMiddleware(
        RequestDelegate next,
        IOptions<IdempotencyOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    /// <summary>
    /// Processes the HTTP request and applies idempotency logic if the endpoint is decorated with [Idempotent].
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="store">The idempotency store (injected per request to support Scoped lifetime).</param>
    public async Task InvokeAsync(HttpContext context, IIdempotencyStore store)
    {
        // Step 1: Check if the endpoint has the [Idempotent] attribute
        var endpoint = context.GetEndpoint();
        var idempotentAttribute = endpoint?.Metadata.GetMetadata<IdempotentAttribute>();

        // If no attribute, skip idempotency logic
        if (idempotentAttribute == null)
        {
            await _next(context);
            return;
        }

        // Step 2: Extract the idempotency key from the header
        if (!context.Request.Headers.TryGetValue(_options.HeaderName, out var idempotencyKey) ||
            string.IsNullOrWhiteSpace(idempotencyKey))
        {
            // No idempotency key provided - proceed normally without idempotency protection
            await _next(context);
            return;
        }

        var key = idempotencyKey.ToString();

        // Step 2b: Validate key format if a validator is configured
        if (_options.KeyValidator != null && !_options.KeyValidator(key))
        {
            context.Response.StatusCode = 400; // Bad Request
            await context.Response.WriteAsync("Invalid Idempotency-Key format.");
            return;
        }

        // Step 3: Check if we have a cached response (Cache Hit)
        var cachedRecord = await store.GetAsync(key, context.RequestAborted);
        if (cachedRecord != null)
        {
            // Step 3a: Validate payload if required
            if (idempotentAttribute.ValidatePayload)
            {
                var currentBodyHash = await ComputeRequestBodyHashAsync(context.Request);
                if (currentBodyHash != cachedRecord.RequestBodyHash)
                {
                    // Same key, different payload - reject with 422 Unprocessable Entity
                    context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                    await context.Response.WriteAsync(
                        "Idempotency key has been used with a different request payload.");
                    return;
                }
            }

            // Step 3b: Replay the cached response
            await ReplayCachedResponseAsync(context, cachedRecord);
            return;
        }

        // Step 4: Try to acquire a distributed lock
        var lockAcquired = await store.TryAcquireLockAsync(key, context.RequestAborted);
        if (!lockAcquired)
        {
            // Another request with the same key is currently being processed
            // Return 409 Conflict
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsync(
                "A request with this idempotency key is currently being processed.");
            return;
        }

        try
        {
            // Step 5: Double-check cache after acquiring lock (race condition protection)
            // Another request might have completed while we were waiting for the lock
            cachedRecord = await store.GetAsync(key, context.RequestAborted);
            if (cachedRecord != null)
            {
                // Validate payload if required
                if (idempotentAttribute.ValidatePayload)
                {
                    var currentBodyHash = await ComputeRequestBodyHashAsync(context.Request);
                    if (currentBodyHash != cachedRecord.RequestBodyHash)
                    {
                        context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                        await context.Response.WriteAsync(
                            "Idempotency key has been used with a different request payload.");
                        return;
                    }
                }

                await ReplayCachedResponseAsync(context, cachedRecord);
                return;
            }

            // Step 6: Compute request body hash for payload validation
            string? requestBodyHash = null;
            if (idempotentAttribute.ValidatePayload)
            {
                requestBodyHash = await ComputeRequestBodyHashAsync(context.Request);
            }

            // Step 7: Execute the request and capture the response (Response Stream Hijacking)
            var originalBodyStream = context.Response.Body;
            using var responseBodyStream = new MemoryStream();

            try
            {
                // Replace the response body stream with our memory stream
                context.Response.Body = responseBodyStream;

                // Execute the rest of the pipeline (the controller action)
                await _next(context);

                // Step 8: Check if the response is successful (2xx status code)
                if (context.Response.StatusCode >= 200 && context.Response.StatusCode < 300)
                {
                    // Capture the response data
                    responseBodyStream.Seek(0, SeekOrigin.Begin);
                    var responseBody = responseBodyStream.ToArray();

                    // Create the idempotency record
                    var record = new IdempotencyRecord
                    {
                        StatusCode = context.Response.StatusCode,
                        Headers = CaptureResponseHeaders(context.Response),
                        Body = responseBody,
                        CreatedAt = DateTime.UtcNow,
                        RequestBodyHash = requestBodyHash
                    };

                    // Save to store
                    var expiryMinutes = idempotentAttribute.ExpiryInMinutes > 0
                        ? idempotentAttribute.ExpiryInMinutes
                        : _options.DefaultExpiryMinutes;

                    await store.SaveAsync(key, record, expiryMinutes, context.RequestAborted);
                }

                // Step 9: Copy the captured response back to the original stream
                responseBodyStream.Seek(0, SeekOrigin.Begin);
                await responseBodyStream.CopyToAsync(originalBodyStream, context.RequestAborted);
            }
            finally
            {
                // Restore the original body stream
                context.Response.Body = originalBodyStream;
            }
        }
        finally
        {
            // Step 10: Always release the lock, even if an exception occurs
            await store.ReleaseLockAsync(key, context.RequestAborted);
        }
    }

    /// <summary>
    /// Replays a cached response to the current HTTP context.
    /// </summary>
    private static async Task ReplayCachedResponseAsync(HttpContext context, IdempotencyRecord record)
    {
        context.Response.StatusCode = record.StatusCode;

        // Restore headers (skip headers that can't be set after response has started)
        foreach (var header in record.Headers)
        {
            if (!context.Response.Headers.ContainsKey(header.Key))
            {
                context.Response.Headers[header.Key] = header.Value;
            }
        }

        // Write the cached body
        if (record.Body.Length > 0)
        {
            await context.Response.Body.WriteAsync(record.Body, context.RequestAborted);
        }
    }

    /// <summary>
    /// Captures the response headers into a dictionary for storage.
    /// </summary>
    private static Dictionary<string, StringValues> CaptureResponseHeaders(HttpResponse response)
    {
        var headers = new Dictionary<string, StringValues>();
        
        foreach (var header in response.Headers)
        {
            // Skip headers that shouldn't be cached or cause issues on replay
            if (ShouldCacheHeader(header.Key))
            {
                headers[header.Key] = header.Value;
            }
        }

        return headers;
    }

    /// <summary>
    /// Determines if a response header should be cached.
    /// Excludes headers that are connection-specific or auto-generated.
    /// </summary>
    private static bool ShouldCacheHeader(string headerName)
    {
        var excludedHeaders = new[]
        {
            "Transfer-Encoding",
            "Connection",
            "Keep-Alive",
            "Upgrade",
            "Date", // Date should reflect the cached response time, not replay time
            "Set-Cookie", // Security: Don't cache session cookies
            "Authorization" // Security: Don't cache auth tokens (though usually in request, sometimes in response)
        };

        return !excludedHeaders.Contains(headerName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Computes a SHA256 hash of the request body for payload validation.
    /// </summary>
    private static async Task<string?> ComputeRequestBodyHashAsync(HttpRequest request)
    {
        // Enable buffering to allow multiple reads of the request body
        request.EnableBuffering();

        // Security: Limit the size of body we are willing to hash to prevent DoS (e.g. 10MB)
        if (request.ContentLength > 10 * 1024 * 1024)
        {
            throw new InvalidOperationException("Request body too large for idempotency payload validation.");
        }

        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(request.Body);
        
        // Reset the stream position for the controller to read
        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        return Convert.ToBase64String(hashBytes);
    }
}
