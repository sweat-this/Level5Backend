using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

/// <summary>
/// One real, ephemeral Postgres container shared by every test in a collection. Real Postgres
/// (not SQLite) is required to exercise JSONB, partial unique indexes, and the conditional
/// UPDATE ... WHERE revision = @expected concurrency check exactly as production runs them.
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

    public Level5V2DbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<Level5V2DbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;

        return new Level5V2DbContext(options);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
