using IdempotencyShield.Configuration;
using IdempotencyShield.EntityFrameworkCore.Entities;
using IdempotencyShield.Extensions; // Required for AddIdempotencyShield
using IdempotencyShield.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IdempotencyShield.EntityFrameworkCore.Extensions;

public static class EfCoreServiceCollectionExtensions
{
    /// <summary>
    /// Adds IdempotencyShield services using Entity Framework Core for storage.
    /// Use this when you want to store idempotency data in a SQL database (SQL Server, Postgres, etc).
    /// </summary>
    /// <typeparam name="TContext">Your DbContext type that implements <see cref="IIdempotencyDbContext"/>.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Delegate to configure idempotency options.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddIdempotencyShieldWithEfCore<TContext>(
        this IServiceCollection services,
        Action<IdempotencyOptions>? configureOptions = null,
        TimeSpan? cleanupInterval = null)
        where TContext : DbContext, IIdempotencyDbContext
    {
        // Add core services (without default store validation, we are adding one)
        services.AddIdempotencyShield(configureOptions ?? (_ => { }));

        // Replace/Register the store implementation
        // Important: Must be Scoped because DbContext is Scoped
        services.AddScoped<IIdempotencyStore, EfCoreIdempotencyStore<TContext>>();

        // Register background cleanup service
        // Default interval is 1 hour if not specified
        var interval = cleanupInterval ?? TimeSpan.FromHours(1);
        services.AddSingleton<IHostedService>(sp => 
            new HostedServices.IdempotencyCleanupHostedService<TContext>(
                sp, 
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<HostedServices.IdempotencyCleanupHostedService<TContext>>>(), 
                interval));

        return services;
    }
}
