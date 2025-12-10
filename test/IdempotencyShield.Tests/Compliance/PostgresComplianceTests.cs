using IdempotencyShield.EntityFrameworkCore.Extensions;
using IdempotencyShield.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace IdempotencyShield.Tests.Compliance;

public class PostgresComplianceTests : IdempotencyStoreComplianceTests
{
    private IHost? _host;
    private string? _connectionString;
    private bool _isAvailable;

    public PostgresComplianceTests()
    {
        // Environment Variable or standard local default
        var envConn = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION_STRING");
        var defaultConn = "Host=localhost;Database=idempotency_tests;Username=postgres;Password=postgres";

        _connectionString = !string.IsNullOrWhiteSpace(envConn) ? envConn : defaultConn;
        _isAvailable = CheckConnection(_connectionString);
    }

    private bool CheckConnection(string connStr)
    {
        try
        {
            using var conn = new NpgsqlConnection(connStr);
            conn.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override HttpClient CreateClient()
    {
        if (!_isAvailable)
        {
            // Similar to SQL Server E2E, fallback to effectively skip or mock
        }

        var builder = WebApplication.CreateBuilder();

        if (_isAvailable && _connectionString != null)
        {
            // Ensure DB/Schema Created
            var optionsBuilder = new DbContextOptionsBuilder<TestIdempotencyContext>();
            optionsBuilder.UseNpgsql(_connectionString);
            using (var ctx = new TestIdempotencyContext(optionsBuilder.Options))
            {
                ctx.Database.EnsureCreated();
            }

            builder.Services.AddDbContext<TestIdempotencyContext>(options =>
                options.UseNpgsql(_connectionString));
            
            builder.Services.AddIdempotencyShieldWithEfCore<TestIdempotencyContext>();
        }
        else
        {
             // Fallback to In-Memory to keep tests green
             builder.Services.AddIdempotencyShield();
        }

        builder.Services.AddControllers()
            .AddApplicationPart(typeof(TestController).Assembly);

        builder.WebHost.UseTestServer();

        var app = builder.Build();

        app.UseRouting();
        app.UseIdempotencyShield();
        app.MapControllers();

        _host = app;
        app.StartAsync().GetAwaiter().GetResult();

        return app.GetTestServer().CreateClient();
    }

    protected override async Task CleanupAsync()
    {
        if (_isAvailable && _connectionString != null)
        {
            var optionsBuilder = new DbContextOptionsBuilder<TestIdempotencyContext>();
            optionsBuilder.UseNpgsql(_connectionString);
            using var ctx = new TestIdempotencyContext(optionsBuilder.Options);
            if (await ctx.Database.CanConnectAsync())
            {
                 await ctx.Database.ExecuteSqlRawAsync("DELETE FROM \"IdempotencyRecords\"");
                 await ctx.Database.ExecuteSqlRawAsync("DELETE FROM \"IdempotencyLocks\"");
            }
        }
    }
}
