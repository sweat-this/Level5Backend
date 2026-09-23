using Level5.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Level5.Api.IntegrationTests;

/// <summary>
/// Boots the real Api host (real middleware pipeline, real DI composition root) against a real,
/// ephemeral Postgres container - the point is to exercise authentication, authorization,
/// validation, and ProblemDetails exactly as they run in production, not a simplified stand-in.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:18-alpine")
        .WithDatabase("level5_v2_api_test")
        .WithUsername("level5")
        .WithPassword("test-password")
        .Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // EnsureCreated rather than Migrate: these tests exercise API behavior, not the
        // migration path itself (that is covered by the Infrastructure integration tests).
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _container.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _container.GetConnectionString(),
                ["Jwt:Key"] = "test-only-signing-key-not-for-production-use-32chars-min",
                ["Jwt:Issuer"] = "Level5BackendV2.Tests",
                ["Jwt:Audience"] = "Level5Client.Tests"
            });
        });
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "Api";
}
