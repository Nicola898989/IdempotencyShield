using System.Security.Cryptography;
using System.Text;
using IdempotencyShield.Attributes;
using IdempotencyShield.Configuration;
using IdempotencyShield.Exceptions;
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
        var endpoint = context.GetEndpoint();
        var idempotentAttribute = endpoint?.Metadata.GetMetadata<IdempotentAttribute>();

        if (idempotentAttribute == null)
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(_options.HeaderName, out var idempotencyKey) ||
            string.IsNullOrWhiteSpace(idempotencyKey))
        {
            await _next(context);
            return;
        }

        var key = idempotencyKey.ToString();

        if (_options.KeyValidator != null && !_options.KeyValidator(key))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Invalid Idempotency-Key format.");
            return;
        }

        // Compute request body hash once if payload validation is enabled.
        string? requestBodyHash = null;
        if (idempotentAttribute.ValidatePayload)
        {
            requestBodyHash = await ComputeRequestBodyHashAsync(context.Request);
        }

        // 1. Check for cached response with resilience
        IdempotencyRecord? cachedRecord = await ExecuteWithResilienceAsync(
            async () => await store.GetAsync(key, context.RequestAborted), 
            fallbackValue: null,
            context.RequestAborted);

        if (cachedRecord != null)
        {
            if (idempotentAttribute.ValidatePayload && requestBodyHash != cachedRecord.RequestBodyHash)
            {
                context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                await context.Response.WriteAsync("Idempotency key has been used with a different request payload.");
                return;
            }

            await ReplayCachedResponseAsync(context, cachedRecord);
            return;
        }

        // 2. Try Acquire Lock with resilience
        // If Fail-Open and store fails, we treat it as "Acquired" (true) to allow processing.
        var lockAcquired = await ExecuteWithResilienceAsync(
            async () => await store.TryAcquireLockAsync(key, _options.LockExpirationMilliseconds, _options.LockWaitTimeoutMilliseconds, context.RequestAborted), 
            fallbackValue: true, 
            context.RequestAborted);

        if (!lockAcquired)
        {
            if (_options.LockWaitTimeoutMilliseconds > 0)
            {
                 // Logic for timeout (didn't acquire lock)
                throw new LockTimeoutException(
                    $"Could not acquire a lock for idempotency key '{key}' within the configured timeout of {_options.LockWaitTimeoutMilliseconds}ms.",
                    key, _options.LockWaitTimeoutMilliseconds);
            }

            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsync("A request with this idempotency key is currently being processed.");
            return;
        }

        try
        {
            // Double check (standard patterns might re-check here, but for now we proceed)
            // Note: If GetAsync failed before (FailOpen), checking again here might fail again.
            // Efficient check: if we are in "FailOpen" mode and the previous GetAsync FAILED (not just returned null),
            // then we should probably skip this second check or at least handle it.
            // Our ExecuteWithResilienceAsync handles exceptions by returning fallback, so calling it again is safe.
            
            cachedRecord = await ExecuteWithResilienceAsync(
                async () => await store.GetAsync(key, context.RequestAborted),
                fallbackValue: null, 
                context.RequestAborted);

            if (cachedRecord != null)
            {
                 if (idempotentAttribute.ValidatePayload && requestBodyHash != cachedRecord.RequestBodyHash)
                {
                    context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                    await context.Response.WriteAsync("Idempotency key has been used with a different request payload.");
                    return;
                }

                await ReplayCachedResponseAsync(context, cachedRecord);
                return;
            }

            await ExecuteAndCacheRequest(context, store, key, idempotentAttribute, requestBodyHash);
        }
        finally
        {
            // Release lock with resilience
            await ExecuteWithResilienceAsync(
                async () => { await store.ReleaseLockAsync(key, context.RequestAborted); return true; }, 
                fallbackValue: true, // Return value ignored for ReleaseLock
                context.RequestAborted);
        }
    }

    private async Task ExecuteAndCacheRequest(HttpContext context, IIdempotencyStore store, string key, IdempotentAttribute idempotentAttribute, string? requestBodyHash)
    {
        var originalBodyStream = context.Response.Body;
        using var responseBodyStream = new MemoryStream();

        try
        {
            context.Response.Body = responseBodyStream;
            await _next(context);

            if (context.Response.StatusCode >= 200 && context.Response.StatusCode < 300)
            {
                responseBodyStream.Seek(0, SeekOrigin.Begin);
                var responseBody = responseBodyStream.ToArray();

                var record = new IdempotencyRecord
                {
                    StatusCode = context.Response.StatusCode,
                    Headers = CaptureResponseHeaders(context.Response),
                    Body = responseBody,
                    CreatedAt = DateTime.UtcNow,
                    RequestBodyHash = requestBodyHash
                };

                var expiryMinutes = idempotentAttribute.ExpiryInMinutes > 0
                    ? idempotentAttribute.ExpiryInMinutes
                    : _options.DefaultExpiryMinutes;

                // Save with resiliency
                // In Fail-Open mode, if Save fails, we swallow the error (log it) and return the response.
                // This means the response is sent to the client, but not cached for future deduplication.
                await ExecuteWithResilienceAsync(
                    async () => { await store.SaveAsync(key, record, expiryMinutes, context.RequestAborted); return true; },
                    fallbackValue: true, 
                    context.RequestAborted);
            }

            responseBodyStream.Seek(0, SeekOrigin.Begin);
            await responseBodyStream.CopyToAsync(originalBodyStream, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBodyStream;
        }
    }

    private async Task ReplayCachedResponseAsync(HttpContext context, IdempotencyRecord record)
    {
        context.Response.StatusCode = record.StatusCode;
        foreach (var header in record.Headers)
        {
            if (!context.Response.Headers.ContainsKey(header.Key))
            {
                context.Response.Headers[header.Key] = header.Value;
            }
        }

        if (record.Body.Length > 0)
        {
            await context.Response.Body.WriteAsync(record.Body, context.RequestAborted);
        }
    }

    private Dictionary<string, StringValues> CaptureResponseHeaders(HttpResponse response)
    {
        return response.Headers
            .Where(header => !_options.ExcludedHeaders.Contains(header.Key))
            .ToDictionary(header => header.Key, header => header.Value);
    }

    private async Task<string?> ComputeRequestBodyHashAsync(HttpRequest request)
    {
        request.EnableBuffering();

        if (request.ContentLength > _options.MaxRequestBodySize)
        {
            throw new RequestBodyTooLargeException(
                $"Request body size ({request.ContentLength} bytes) exceeds the configured limit ({_options.MaxRequestBodySize} bytes) for idempotency payload validation.",
                request.ContentLength ?? 0,
                _options.MaxRequestBodySize);
        }

        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(request.Body);

        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Executes a storage operation with retry logic and failure mode handling.
    /// </summary>
    private async Task<T> ExecuteWithResilienceAsync<T>(
        Func<Task<T>> operation, 
        T fallbackValue, 
        CancellationToken cancellationToken)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (!IsCancellationError(ex)) 
            {
                attempt++;
                
                // If we haven't exhausted retries, wait and retry
                if (attempt <= _options.StorageRetryCount)
                {
                    // Simple constant delay
                    await Task.Delay(_options.StorageRetryDelayMilliseconds, cancellationToken);
                    continue; 
                }

                // Retries exhausted. Check FailureMode.
                if (_options.FailureMode == IdempotencyFailureMode.FailOpen)
                {
                    // Log warning (assuming logger was available, but we don't have ILogger injected yet in this minimalist version)
                    // TODO: Inject ILogger<IdempotencyMiddleware> to log these failures.
                    // For now, we swallow and return fallback.
                    return fallbackValue;
                }

                // FailSafe: Propagate exception
                throw;
            }
        }
    }

    private static bool IsCancellationError(Exception ex)
    {
        return ex is OperationCanceledException;
    }
}
