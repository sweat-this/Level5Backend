using Level5.LegacyScoreMigration.Commands;
using Level5.LegacyScoreMigration.Legacy;
using Microsoft.EntityFrameworkCore;

namespace Level5.LegacyScoreMigration.IntegrationTests;

[Collection(ScoreMigrationTestCollection.Name)]
public sealed class VerifyCommandTests(LegacyPostgresFixture legacyFixture, V2PostgresFixture v2Fixture)
{
    [Fact]
    public async Task Verify_reports_not_migrated_for_a_row_with_no_provenance_link()
    {
        var legacyUserId = 666_001;
        await Seed(legacyUserId);
        var legacyHighscoreId = await legacyFixture.InsertHighscoreAsync(userid: legacyUserId, scoreid: Guid.NewGuid().ToString("N"));

        await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
        var exitCode = await VerifyCommand.RunAsync(
            new LegacyHighscoreReader(legacyFixture.ConnectionString), provider, new VerifyOptions(legacyHighscoreId), CancellationToken.None);

        // NotMigrated is not Inconsistent - verify's exit code stays clean for a row that was never migrated.
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Verify_reports_consistent_for_a_cleanly_migrated_row_and_is_read_only()
    {
        var legacyUserId = 666_002;
        await Seed(legacyUserId);
        var legacyHighscoreId = await legacyFixture.InsertHighscoreAsync(userid: legacyUserId, scoreid: Guid.NewGuid().ToString("N"));

        await using (var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString))
        {
            var migrateExitCode = await MigrateCommand.RunAsync(
                new LegacyHighscoreReader(legacyFixture.ConnectionString), provider, new MigrateOptions(legacyHighscoreId, Limit: null), CancellationToken.None);
            Assert.Equal(0, migrateExitCode);
        }

        await using var db = v2Fixture.CreateDbContext();
        var resultCountBefore = await db.MatchResults.CountAsync();
        var linkCountBefore = await db.LegacyMatchResultLinks.CountAsync();

        await using var provider2 = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
        var exitCode = await VerifyCommand.RunAsync(
            new LegacyHighscoreReader(legacyFixture.ConnectionString), provider2, new VerifyOptions(legacyHighscoreId), CancellationToken.None);

        Assert.Equal(0, exitCode);

        await using var afterDb = v2Fixture.CreateDbContext();
        Assert.Equal(resultCountBefore, await afterDb.MatchResults.CountAsync());
        Assert.Equal(linkCountBefore, await afterDb.LegacyMatchResultLinks.CountAsync());
    }

    [Fact]
    public async Task Verify_reports_inconsistent_when_provenance_no_longer_resolves_to_the_expected_owner()
    {
        var legacyUserId = 666_003;
        var (_, playerId) = await Seed(legacyUserId);
        var legacyHighscoreId = await legacyFixture.InsertHighscoreAsync(userid: legacyUserId, scoreid: Guid.NewGuid().ToString("N"));

        await using (var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString))
        {
            var migrateExitCode = await MigrateCommand.RunAsync(
                new LegacyHighscoreReader(legacyFixture.ConnectionString), provider, new MigrateOptions(legacyHighscoreId, Limit: null), CancellationToken.None);
            Assert.Equal(0, migrateExitCode);
        }

        try
        {
            await using (var db = v2Fixture.CreateDbContext())
            {
                var otherPlayerId = await TestSeeding.SeedUnlinkedPlayerAsync(db, "verifycorrupt");
                var link = await db.LegacyAccountLinks.SingleAsync(l => l.LegacyUserId == legacyUserId);
                link.PlayerId = otherPlayerId;
                await db.SaveChangesAsync();
            }

            await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
            var exitCode = await VerifyCommand.RunAsync(
                new LegacyHighscoreReader(legacyFixture.ConnectionString), provider, new VerifyOptions(legacyHighscoreId), CancellationToken.None);

            Assert.Equal(1, exitCode);
        }
        finally
        {
            await using var cleanupDb = v2Fixture.CreateDbContext();
            var link = await cleanupDb.LegacyAccountLinks.SingleAsync(l => l.LegacyUserId == legacyUserId);
            link.PlayerId = playerId;
            await cleanupDb.SaveChangesAsync();
        }
    }

    private async Task<(Guid AccountId, Guid PlayerId)> Seed(int legacyUserId)
    {
        await using var db = v2Fixture.CreateDbContext();
        return await TestSeeding.SeedLinkedAccountAsync(db, legacyUserId, "verify");
    }
}
