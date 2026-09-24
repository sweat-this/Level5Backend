using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Testcontainers.PostgreSql;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

/// <summary>
/// One real, ephemeral Postgres container shared by every test in a collection. Real Postgres
/// (not SQLite) is required to exercise JSONB, partial unique indexes, and the conditional
/// UPDATE ... WHERE revision = @expected concurrency check exactly as production runs them. Contexts
/// are built with the same runtime configuration as the API (<see cref="PostgresConfiguration"/>,
/// including its retrying execution strategy), so every store test also proves its persistence
/// path is compatible with that strategy.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgres:18-alpine")
            .WithDatabase("level5_v2_test")
            .WithUsername("level5")
            .WithPassword("test-password")
            .Build();

        await _container.StartAsync();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public Level5V2DbContext CreateDbContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<Level5V2DbContext>()
            .UseLevel5Postgres(_container.GetConnectionString())
            .AddInterceptors(interceptors)
            .Options;

        return new Level5V2DbContext(options);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
