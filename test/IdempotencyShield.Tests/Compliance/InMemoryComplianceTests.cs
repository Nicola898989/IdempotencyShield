using IdempotencyShield.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IdempotencyShield.Tests.Compliance;

public class InMemoryComplianceTests : IdempotencyStoreComplianceTests
{
    private IHost? _host;

    protected override HttpClient CreateClient()
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services.AddIdempotencyShield(); // Default is InMemory
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

    protected override Task CleanupAsync()
    {
        // For In-Memory, creating a new host/client (which CreateClient does) 
        // effectively gives us a fresh store.
        // But if we want to be explicit, we could Dispose _host.
        _host?.Dispose();
        return Task.CompletedTask;
    }
}
