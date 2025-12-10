using IdempotencyShield.Configuration;
using IdempotencyShield.Middleware;
using IdempotencyShield.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace IdempotencyShield.Extensions;

/// <summary>
/// Extension methods for configuring IdempotencyShield services and middleware.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds IdempotencyShield services to the dependency injection container with default configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddIdempotencyShield(this IServiceCollection services)
    {
        return services.AddIdempotencyShield(_ => { });
    }

    /// <summary>
    /// Adds IdempotencyShield services to the dependency injection container with custom configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Action to configure IdempotencyOptions.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddIdempotencyShield(
        this IServiceCollection services,
        Action<IdempotencyOptions> configureOptions)
    {
        // Register configuration
        services.Configure(configureOptions);

        // Register the default in-memory store
        // For production, replace with Redis or database-backed implementation
        services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();

        return services;
    }

    /// <summary>
    /// Adds IdempotencyShield services with a custom store implementation.
    /// </summary>
    /// <typeparam name="TStore">The type of the custom store implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Action to configure IdempotencyOptions.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddIdempotencyShield<TStore>(
        this IServiceCollection services,
        Action<IdempotencyOptions>? configureOptions = null)
        where TStore : class, IIdempotencyStore
    {
        // Register configuration
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        // Register the custom store
        services.AddSingleton<IIdempotencyStore, TStore>();

        return services;
    }
}

/// <summary>
/// Extension methods for configuring IdempotencyShield middleware in the HTTP pipeline.
/// </summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the IdempotencyShield middleware to the application pipeline.
    /// This should be added before UseRouting() and UseEndpoints() to intercept requests early.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseIdempotencyShield(this IApplicationBuilder app)
    {
        return app.UseMiddleware<IdempotencyMiddleware>();
    }
}
