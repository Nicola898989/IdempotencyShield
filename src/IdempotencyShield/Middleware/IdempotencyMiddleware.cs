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

        var cachedRecord = await store.GetAsync(key, context.RequestAborted);
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

        if (!await store.TryAcquireLockAsync(key, _options.LockExpirationMilliseconds, _options.LockWaitTimeoutMilliseconds, context.RequestAborted))
        {
            if (_options.LockWaitTimeoutMilliseconds > 0)
            {
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
            cachedRecord = await store.GetAsync(key, context.RequestAborted);
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
            await store.ReleaseLockAsync(key, context.RequestAborted);
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

                await store.SaveAsync(key, record, expiryMinutes, context.RequestAborted);
            }

            responseBodyStream.Seek(0, SeekOrigin.Begin);
            await responseBodyStream.CopyToAsync(originalBodyStream, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBodyStream;
        }
    }

    private static async Task ReplayCachedResponseAsync(HttpContext context, IdempotencyRecord record)
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
}
