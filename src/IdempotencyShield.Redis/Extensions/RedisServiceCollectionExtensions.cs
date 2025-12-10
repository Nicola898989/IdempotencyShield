using IdempotencyShield.Configuration;
using IdempotencyShield.Redis;
using IdempotencyShield.Storage;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace IdempotencyShield.Extensions;

/// <summary>
/// Extension methods for configuring IdempotencyShield with Redis storage.
/// </summary>
public static class RedisServiceCollectionExtensions
{
    /// <summary>
    /// Adds IdempotencyShield services with Redis storage backend.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="redisConfiguration">Redis connection string (e.g., "localhost:6379").</param>
    /// <param name="configureOptions">Optional action to configure IdempotencyOptions.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddIdempotencyShieldWithRedis(
        this IServiceCollection services,
        string redisConfiguration,
        Action<IdempotencyOptions>? configureOptions = null)
    {
        if (string.IsNullOrWhiteSpace(redisConfiguration))
        {
            throw new ArgumentException("Redis configuration cannot be null or empty.", nameof(redisConfiguration));
        }

        // Register Redis connection
        var redis = ConnectionMultiplexer.Connect(redisConfiguration);
        services.AddSingleton<IConnectionMultiplexer>(redis);

        // Register Redis store
        services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();

        // Register configuration
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        return services;
    }

    /// <summary>
    /// Adds IdempotencyShield services with Redis storage using an existing IConnectionMultiplexer.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional action to configure IdempotencyOptions.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Use this overload when you already have an IConnectionMultiplexer registered in DI.
    /// </remarks>
    public static IServiceCollection AddIdempotencyShieldWithRedis(
        this IServiceCollection services,
        Action<IdempotencyOptions>? configureOptions = null)
    {
        // Register Redis store (expects IConnectionMultiplexer to be already registered)
        services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();

        // Register configuration
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        return services;
    }

    /// <summary>
    /// Adds IdempotencyShield services with Redis storage using ConfigurationOptions.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="redisOptions">Redis configuration options.</param>
    /// <param name="configureOptions">Optional action to configure IdempotencyOptions.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddIdempotencyShieldWithRedis(
        this IServiceCollection services,
        ConfigurationOptions redisOptions,
        Action<IdempotencyOptions>? configureOptions = null)
    {
        if (redisOptions == null)
        {
            throw new ArgumentNullException(nameof(redisOptions));
        }

        // Register Redis connection
        var redis = ConnectionMultiplexer.Connect(redisOptions);
        services.AddSingleton<IConnectionMultiplexer>(redis);

        // Register Redis store
        services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();

        // Register configuration
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        return services;
    }
}
