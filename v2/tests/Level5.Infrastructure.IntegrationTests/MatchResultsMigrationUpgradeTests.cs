using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
    private const string AddMatchResultsMigrationId = "20260923135657_AddMatchResults";

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

    /// <summary>
    /// Proves the ConvertMatchResultModeAndLevelIdsToInteger migration is a genuine fix-forward
    /// conversion: a match_results row persisted under PR #33's original varchar(64) ModeId/LevelId
    /// schema, with real numeric-string values, upgrades cleanly to the integer columns with its
    /// data intact - not silently dropped, zeroed, or truncated.
    /// </summary>
    [Fact]
    public async Task Upgrading_from_the_varchar_ModeId_LevelId_schema_converts_existing_numeric_string_values_to_integers()
    {
        var container = new PostgreSqlBuilder()
            .WithImage("postgres:18-alpine")
            .WithDatabase("level5_v2_modeid_upgrade_test")
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
                await migrator.MigrateAsync(AddMatchResultsMigrationId);
            }

            Guid playerId;
            Guid matchResultId;
            var now = DateTimeOffset.UtcNow;
            await using (var db = new Level5V2DbContext(options))
            {
                playerId = (await PlayerSeeding.CreatePlayerAsync(db, "PreConvert", now)).Value;
                matchResultId = Guid.NewGuid();

                // Written directly via raw SQL against the pre-conversion (varchar ModeId/LevelId)
                // schema - the current MatchResultRow/domain types are already int-typed, so they
                // cannot express the row shape this migration is meant to upgrade from.
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO match_results
                         ("Id", "PlayerId", "ClientResultId", "ModeId", "LevelId", "CharacterId", "ClientVersion", "Platform", "MetricsJson", "ModifiersJson", "CreatedAt")
                     VALUES
                         ({matchResultId}, {playerId}, {Guid.NewGuid()}, {"7"}, {"3"}, {"hero"}, {"1.0.0"}, {"ios"},
                          {"{\"TotalPoints\":90}"}::jsonb, {"{\"Hardcore\":false,\"TrafficEnabled\":false,\"EnemiesEnabled\":false,\"SniperEnabled\":false}"}::jsonb, {now})
                     """);
            }

            await using (var db = new Level5V2DbContext(options))
            {
                await db.Database.MigrateAsync();
            }

            await using (var db = new Level5V2DbContext(options))
            {
                var row = await db.MatchResults.AsNoTracking().SingleAsync(r => r.Id == matchResultId);
                Assert.Equal(7, row.ModeId);
                Assert.Equal(3, row.LevelId);

                var pending = await db.Database.GetPendingMigrationsAsync();
                Assert.Empty(pending);
            }
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    /// <summary>
    /// Proves the ConvertMatchResultModeAndLevelIdsToInteger migration fails loudly on a legacy
    /// varchar ModeId/LevelId value that isn't a base-10 integer, rather than silently coercing it
    /// to 0 or dropping the row - the migration's own explicit `::integer` cast is what enforces
    /// this, so this test proves that cast actually rejects bad data at migration time.
    /// </summary>
    [Fact]
    public async Task Upgrading_from_the_varchar_ModeId_LevelId_schema_fails_loudly_on_a_non_numeric_value()
    {
        var container = new PostgreSqlBuilder()
            .WithImage("postgres:18-alpine")
            .WithDatabase("level5_v2_modeid_upgrade_bad_data_test")
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
                await migrator.MigrateAsync(AddMatchResultsMigrationId);
            }

            var now = DateTimeOffset.UtcNow;
            await using (var db = new Level5V2DbContext(options))
            {
                var playerId = (await PlayerSeeding.CreatePlayerAsync(db, "PreConvertBadData", now)).Value;

                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO match_results
                         ("Id", "PlayerId", "ClientResultId", "ModeId", "LevelId", "CharacterId", "ClientVersion", "Platform", "MetricsJson", "ModifiersJson", "CreatedAt")
                     VALUES
                         ({Guid.NewGuid()}, {playerId}, {Guid.NewGuid()}, {"not-a-number"}, {"3"}, {"hero"}, {"1.0.0"}, {"ios"},
                          {"{\"TotalPoints\":90}"}::jsonb, {"{\"Hardcore\":false,\"TrafficEnabled\":false,\"EnemiesEnabled\":false,\"SniperEnabled\":false}"}::jsonb, {now})
                     """);
            }

            await using (var db = new Level5V2DbContext(options))
            {
                await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
            }

            await using (var db = new Level5V2DbContext(options))
            {
                var pending = await db.Database.GetPendingMigrationsAsync();
                Assert.NotEmpty(pending);
            }
        }
        finally
        {
            await container.DisposeAsync();
        }
    }
}
