using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Level5.LegacyScoreMigration.IntegrationTests;

/// <summary>
/// One real, ephemeral V2 Postgres container per test collection, with real EF migrations applied
/// (including AddLegacyMatchResultLinks) - mirrors Level5.Infrastructure.IntegrationTests.PostgresFixture.
/// Deliberately duplicated rather than referenced from that test project: test-project-to-test-project
/// references aren't used elsewhere in this repo, and this fixture is small enough that duplication
/// costs less than the coupling.
/// </summary>
public sealed class V2PostgresFixture : IAsyncLifetime
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

    public string ConnectionString => _container.GetConnectionString();

    public Level5V2DbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<Level5V2DbContext>()
            .UseLevel5Postgres(_container.GetConnectionString())
            .Options;

        return new Level5V2DbContext(options);
    }
}
