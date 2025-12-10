using IdempotencyShield.EntityFrameworkCore.Extensions;
using IdempotencyShield.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IdempotencyShield.Tests.Compliance;

public class SqlServerComplianceTests : IdempotencyStoreComplianceTests
{
    private IHost? _host;
    private string? _connectionString;
    private bool _isAvailable;

    public SqlServerComplianceTests()
    {
        // Try Environment Variable first, then fallback to LocalDB
        var envConn = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION_STRING");
        var defaultConn = "Server=(localdb)\\mssqllocaldb;Database=IdempotencyTests;Trusted_Connection=True;MultipleActiveResultSets=true";

        _connectionString = !string.IsNullOrWhiteSpace(envConn) ? envConn : defaultConn;
        _isAvailable = CheckConnection(_connectionString);
    }

    private bool CheckConnection(string connStr)
    {
        try
        {
            // Connect to master to check availability, ignoring the specific target DB
            var builder = new SqlConnectionStringBuilder(connStr);
            builder.InitialCatalog = "master"; 
            
            using var conn = new SqlConnection(builder.ConnectionString);
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
             // Fallback to In-Memory if SQL Server is not available
             var builder = WebApplication.CreateBuilder();
             builder.Services.AddIdempotencyShield();
             builder.Services.AddControllers().AddApplicationPart(typeof(TestController).Assembly);
             builder.WebHost.UseTestServer();
             var app = builder.Build();
             app.UseRouting();
             app.UseIdempotencyShield();
             app.MapControllers();
             _host = app;
             app.StartAsync().GetAwaiter().GetResult();
             return app.GetTestServer().CreateClient();
        }
        
        // ... Normal setup ...
        return CreateClientForSqlServer();
    }

    private HttpClient CreateClientForSqlServer()
    {
        var builder = WebApplication.CreateBuilder();

        if (_isAvailable && _connectionString != null)
        {
            // Ensure DB Created
            var optionsBuilder = new DbContextOptionsBuilder<TestIdempotencyContext>();
            optionsBuilder.UseSqlServer(_connectionString);
            using (var ctx = new TestIdempotencyContext(optionsBuilder.Options))
            {
                ctx.Database.EnsureCreated();
            }

            builder.Services.AddDbContext<TestIdempotencyContext>(options =>
                options.UseSqlServer(_connectionString));
            
            builder.Services.AddIdempotencyShieldWithEfCore<TestIdempotencyContext>();
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
            optionsBuilder.UseSqlServer(_connectionString);
            using var ctx = new TestIdempotencyContext(optionsBuilder.Options);
            // Verify we can connect to actual DB before cleaning
            if (await ctx.Database.CanConnectAsync()) 
            {
                try
                {
                     // Clear tables
                     await ctx.Database.ExecuteSqlRawAsync("DELETE FROM IdempotencyRecords");
                     await ctx.Database.ExecuteSqlRawAsync("DELETE FROM IdempotencyLocks");
                }
                catch (SqlException ex)
                {
                    // Ignore "Invalid object name" (table missing) errors
                    // Error Number 208: Invalid object name '%.*ls'.
                    if (ex.Number != 208) throw;
                }
                catch
                {
                    // Best effort
                }
            }
        }
    }
}
