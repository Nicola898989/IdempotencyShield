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
    private string? _dbFilePath;

    protected override HttpClient CreateClient()
    {
        var builder = WebApplication.CreateBuilder();

        // Setup EF Core Sqlite (File-based for better concurrency)
        _dbFilePath = Path.GetTempFileName();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString() + ";Default Timeout=5;Pooling=False"; // Wait up to 5s, no pooling to release locks

        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        
        // Jounal mode WAL is better for concurrency
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 30000; PRAGMA synchronous = NORMAL;";
            command.ExecuteNonQuery();
        }

        builder.Services.AddDbContext<TestIdempotencyContext>(options =>
            options.UseSqlite(connectionString));

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
        
        if (!string.IsNullOrEmpty(_dbFilePath) && File.Exists(_dbFilePath))
        {
            try 
            {
                File.Delete(_dbFilePath);
            }
            catch { /* Ignore cleanup errors */ }
        }
        
        return Task.CompletedTask;
    }
}
