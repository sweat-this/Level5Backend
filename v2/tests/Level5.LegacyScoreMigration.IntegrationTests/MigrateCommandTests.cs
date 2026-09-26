using Level5.LegacyScoreMigration.Commands;
using Level5.LegacyScoreMigration.Legacy;
using Microsoft.EntityFrameworkCore;

namespace Level5.LegacyScoreMigration.IntegrationTests;

[Collection(ScoreMigrationTestCollection.Name)]
public sealed class MigrateCommandTests(LegacyPostgresFixture legacyFixture, V2PostgresFixture v2Fixture)
{
    [Fact]
    public async Task A_clean_row_imports_and_a_row_with_no_account_link_is_blocked()
    {
        var linkedUserId = 777_001;
        await Seed(linkedUserId);
        var cleanId = await legacyFixture.InsertHighscoreAsync(userid: linkedUserId, scoreid: Guid.NewGuid().ToString("N"));
        var blockedId = await legacyFixture.InsertHighscoreAsync(userid: 777_999, scoreid: Guid.NewGuid().ToString("N"));

        await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
        var exitCode = await MigrateCommand.RunAsync(
            new LegacyHighscoreReader(legacyFixture.ConnectionString), provider, new MigrateOptions(LegacyHighscoreId: null, Limit: null), CancellationToken.None);

        Assert.Equal(0, exitCode);

        await using var db = v2Fixture.CreateDbContext();
        Assert.True(await db.LegacyMatchResultLinks.AnyAsync(l => l.LegacyHighscoreId == cleanId));
        Assert.False(await db.LegacyMatchResultLinks.AnyAsync(l => l.LegacyHighscoreId == blockedId));
    }

    [Fact]
    public async Task Interrupted_migration_resumes_cleanly()
    {
        var legacyUserId = 777_002;
        await Seed(legacyUserId);
        var firstId = await legacyFixture.InsertHighscoreAsync(userid: legacyUserId, scoreid: Guid.NewGuid().ToString("N"));
        var secondId = await legacyFixture.InsertHighscoreAsync(userid: legacyUserId, scoreid: Guid.NewGuid().ToString("N"));

        // Simulates an interruption: only the first row (scoped explicitly) gets migrated.
        await using (var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString))
        {
            var exitCode = await MigrateCommand.RunAsync(
                new LegacyHighscoreReader(legacyFixture.ConnectionString), provider, new MigrateOptions(firstId, Limit: null), CancellationToken.None);
            Assert.Equal(0, exitCode);
        }

        await using (var db = v2Fixture.CreateDbContext())
        {
            Assert.True(await db.LegacyMatchResultLinks.AnyAsync(l => l.LegacyHighscoreId == firstId));
            Assert.False(await db.LegacyMatchResultLinks.AnyAsync(l => l.LegacyHighscoreId == secondId));
        }

        // The resumed run scopes to both rows: the first is a no-op (AlreadyLinkedConsistent), the
        // second imports fresh - a rerun never undoes or duplicates the first row's work.
        await using (var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString))
        {
            var exitCode = await MigrateCommand.RunAsync(
                new LegacyHighscoreReader(legacyFixture.ConnectionString), provider, new MigrateOptions(LegacyHighscoreId: null, Limit: null), CancellationToken.None);
            Assert.Equal(0, exitCode);
        }

        await using var finalDb = v2Fixture.CreateDbContext();
        Assert.True(await finalDb.LegacyMatchResultLinks.AnyAsync(l => l.LegacyHighscoreId == firstId));
        Assert.True(await finalDb.LegacyMatchResultLinks.AnyAsync(l => l.LegacyHighscoreId == secondId));
        Assert.Equal(1, await finalDb.LegacyMatchResultLinks.CountAsync(l => l.LegacyHighscoreId == firstId));
    }

    [Fact]
    public async Task Provenance_corruption_aborts_the_run_explicitly()
    {
        var legacyUserId = 777_003;
        var (_, playerId) = await Seed(legacyUserId);
        var legacyHighscoreId = await legacyFixture.InsertHighscoreAsync(userid: legacyUserId, scoreid: Guid.NewGuid().ToString("N"));

        // Migrate it cleanly first, then corrupt the mapping by re-pointing legacy_account_links'
        // PlayerId elsewhere - the exact "manual/partial data tampering" scenario the checker exists
        // to catch. A subsequent (re)migrate of this row must abort rather than silently proceed.
        await using (var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString))
        {
            var exitCode = await MigrateCommand.RunAsync(
                new LegacyHighscoreReader(legacyFixture.ConnectionString), provider, new MigrateOptions(legacyHighscoreId, Limit: null), CancellationToken.None);
            Assert.Equal(0, exitCode);
        }

        await using (var db = v2Fixture.CreateDbContext())
        {
            var otherPlayerId = await TestSeeding.SeedUnlinkedPlayerAsync(db, "corrupt");
            var link = await db.LegacyAccountLinks.SingleAsync(l => l.LegacyUserId == legacyUserId);
            link.PlayerId = otherPlayerId;
            await db.SaveChangesAsync();
        }

        try
        {
            await using var abortProvider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
            var abortExitCode = await MigrateCommand.RunAsync(
                new LegacyHighscoreReader(legacyFixture.ConnectionString), abortProvider, new MigrateOptions(legacyHighscoreId, Limit: null), CancellationToken.None);

            Assert.Equal(2, abortExitCode);
        }
        finally
        {
            // Restores this shared collection's V2 state so any later, unscoped migrate/verify run
            // in another test never trips over this deliberately-corrupted row.
            await using var cleanupDb = v2Fixture.CreateDbContext();
            var link = await cleanupDb.LegacyAccountLinks.SingleAsync(l => l.LegacyUserId == legacyUserId);
            link.PlayerId = playerId;
            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task V1_receives_no_writes()
    {
        var legacyUserId = 777_004;
        await Seed(legacyUserId);
        await legacyFixture.InsertHighscoreAsync(userid: legacyUserId, scoreid: Guid.NewGuid().ToString("N"));
        var countBefore = await legacyFixture.CountHighscoresAsync();

        await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
        await MigrateCommand.RunAsync(
            new LegacyHighscoreReader(legacyFixture.ConnectionString), provider, new MigrateOptions(LegacyHighscoreId: null, Limit: null), CancellationToken.None);

        Assert.Equal(countBefore, await legacyFixture.CountHighscoresAsync());
    }

    private async Task<(Guid AccountId, Guid PlayerId)> Seed(int legacyUserId)
    {
        await using var db = v2Fixture.CreateDbContext();
        return await TestSeeding.SeedLinkedAccountAsync(db, legacyUserId, "migrate");
    }
}
