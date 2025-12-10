using IdempotencyShield.EntityFrameworkCore.Extensions;
using IdempotencyShield.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IdempotencyShield.Tests.Compliance;

public class EfCoreComplianceTests : IdempotencyStoreComplianceTests
{
    private IHost? _host;
    private SqliteConnection? _connection;

    protected override HttpClient CreateClient()
    {
        var builder = WebApplication.CreateBuilder();

        // Setup EF Core Sqlite (In-Memory shared connection)
        // Setup EF Core Sqlite (In-Memory shared connection)
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        
        // Set busy timeout to 5 seconds to handle concurrent tests
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA busy_timeout = 5000;";
            command.ExecuteNonQuery();
        }

        builder.Services.AddDbContext<TestIdempotencyContext>(options =>
            options.UseSqlite(_connection));

        builder.Services.AddIdempotencyShieldWithEfCore<TestIdempotencyContext>();
        
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(TestController).Assembly);

        builder.WebHost.UseTestServer();

        var app = builder.Build();

        // Ensure Schema Created
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestIdempotencyContext>();
            db.Database.EnsureCreated();
        }

        app.UseRouting();
        app.UseIdempotencyShield();
        app.MapControllers();

        _host = app;
        app.StartAsync().GetAwaiter().GetResult();

        return app.GetTestServer().CreateClient();
    }

    protected override Task CleanupAsync()
    {
        _host?.Dispose();
        _connection?.Close();
        _connection?.Dispose();
        return Task.CompletedTask;
    }
}
