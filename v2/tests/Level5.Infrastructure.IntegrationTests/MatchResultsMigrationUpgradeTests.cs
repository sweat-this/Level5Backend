using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

/// <summary>
/// Proves the AddMatchResults migration is a genuine forward migration: a database already
/// upgraded to the prior (InitialCreate-only) schema, with real pre-existing data, upgrades
/// cleanly to the current schema without losing that data - not just that a brand-new database
/// can apply the full chain (which <see cref="PostgresFixture"/>/<see cref="SchemaTests"/> already
/// cover). Runs against its own dedicated container so it can control exactly which migrations are
/// applied at each step, independent of the shared <see cref="PostgresFixture"/> container which
/// is always migrated straight to the latest schema.
/// </summary>
public sealed class MatchResultsMigrationUpgradeTests
{
    private const string InitialCreateMigrationId = "20260917160146_InitialCreate";

    [Fact]
    public async Task Upgrading_from_the_initial_schema_to_the_current_schema_preserves_existing_data_and_adds_match_results()
    {
        var container = new PostgreSqlBuilder()
            .WithImage("postgres:18-alpine")
            .WithDatabase("level5_v2_upgrade_test")
            .WithUsername("level5")
            .WithPassword("test-password")
            .Build();
        await container.StartAsync();

        try
        {
            var options = new DbContextOptionsBuilder<Level5V2DbContext>().UseNpgsql(container.GetConnectionString()).Options;

            await using (var db = new Level5V2DbContext(options))
            {
                var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrator.MigrateAsync(InitialCreateMigrationId);
            }

            Guid playerId;
            var now = DateTimeOffset.UtcNow;
            await using (var db = new Level5V2DbContext(options))
            {
                playerId = (await PlayerSeeding.CreatePlayerAsync(db, "PreUpgrade", now)).Value;
            }

            await using (var db = new Level5V2DbContext(options))
            {
                await db.Database.MigrateAsync();
            }

            await using (var db = new Level5V2DbContext(options))
            {
                var stillThere = await db.PlayerProfiles.SingleAsync(p => p.Id == playerId);
                Assert.Equal(playerId, stillThere.Id);

                var tables = await db.Database.SqlQuery<string>(
                        $"SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'")
                    .ToListAsync();
                Assert.Contains("match_results", tables);

                var pending = await db.Database.GetPendingMigrationsAsync();
                Assert.Empty(pending);
            }
        }
        finally
        {
            await container.DisposeAsync();
        }
    }
}
