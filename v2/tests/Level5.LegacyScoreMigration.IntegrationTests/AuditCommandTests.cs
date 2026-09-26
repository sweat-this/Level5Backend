using Level5.LegacyScoreMigration.Commands;
using Level5.LegacyScoreMigration.Legacy;
using Microsoft.EntityFrameworkCore;

namespace Level5.LegacyScoreMigration.IntegrationTests;

[Collection(ScoreMigrationTestCollection.Name)]
public sealed class AuditCommandTests(LegacyPostgresFixture legacyFixture, V2PostgresFixture v2Fixture)
{
    [Fact]
    public async Task Audit_never_writes_to_v2_regardless_of_findings()
    {
        await legacyFixture.InsertHighscoreAsync(userid: 999_001, scoreid: Guid.NewGuid().ToString("N"));
        await legacyFixture.InsertHighscoreAsync(userid: 999_002, scoreid: "not-a-guid");

        await using var db = v2Fixture.CreateDbContext();
        var resultCountBefore = await db.MatchResults.CountAsync();
        var linkCountBefore = await db.LegacyMatchResultLinks.CountAsync();
        var v1CountBefore = await legacyFixture.CountHighscoresAsync();

        await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
        var exitCode = await AuditCommand.RunAsync(
            new LegacyHighscoreReader(legacyFixture.ConnectionString), provider, new AuditOptions(LegacyHighscoreId: null, Limit: null, Verbose: true), CancellationToken.None);

        Assert.True(exitCode is 0 or 1);

        await using var afterDb = v2Fixture.CreateDbContext();
        Assert.Equal(resultCountBefore, await afterDb.MatchResults.CountAsync());
        Assert.Equal(linkCountBefore, await afterDb.LegacyMatchResultLinks.CountAsync());
        Assert.Equal(v1CountBefore, await legacyFixture.CountHighscoresAsync());
    }

    [Fact]
    public async Task Audit_reports_a_missing_account_link_as_blocked_with_nonzero_exit()
    {
        var legacyHighscoreId = await legacyFixture.InsertHighscoreAsync(userid: 888_001, scoreid: Guid.NewGuid().ToString("N"));

        await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
        var exitCode = await AuditCommand.RunAsync(
            new LegacyHighscoreReader(legacyFixture.ConnectionString), provider, new AuditOptions(legacyHighscoreId, Limit: null, Verbose: false), CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Audit_reports_a_clean_row_with_zero_exit_when_scoped_to_it_alone()
    {
        var legacyUserId = 888_002;
        var (accountId, playerId) = await SeedLinkedAccountAsync(legacyUserId);
        var legacyHighscoreId = await legacyFixture.InsertHighscoreAsync(userid: legacyUserId, scoreid: Guid.NewGuid().ToString("N"));
        _ = (accountId, playerId);

        await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
        var exitCode = await AuditCommand.RunAsync(
            new LegacyHighscoreReader(legacyFixture.ConnectionString), provider, new AuditOptions(legacyHighscoreId, Limit: null, Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Audit_reports_a_malformed_scoreid_as_blocked_without_writing_anything()
    {
        var legacyUserId = 888_003;
        await SeedLinkedAccountAsync(legacyUserId);
        var legacyHighscoreId = await legacyFixture.InsertHighscoreAsync(userid: legacyUserId, scoreid: "not-a-guid");

        var originalOut = Console.Out;
        try
        {
            using var writer = new StringWriter();
            Console.SetOut(writer);

            await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
            var exitCode = await AuditCommand.RunAsync(
                new LegacyHighscoreReader(legacyFixture.ConnectionString), provider, new AuditOptions(legacyHighscoreId, Limit: null, Verbose: true), CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Contains("BlockedMissingOrMalformedScoreid", writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task Audit_reports_date_classification_as_a_diagnostic_that_never_affects_the_exit_code()
    {
        var legacyUserId = 888_004;
        await SeedLinkedAccountAsync(legacyUserId);
        var legacyHighscoreId = await legacyFixture.InsertHighscoreAsync(
            userid: legacyUserId, scoreid: Guid.NewGuid().ToString("N"), date: "9/25/2026 1:23:45 PM");

        var originalOut = Console.Out;
        try
        {
            using var writer = new StringWriter();
            Console.SetOut(writer);

            await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
            var exitCode = await AuditCommand.RunAsync(
                new LegacyHighscoreReader(legacyFixture.ConnectionString), provider, new AuditOptions(legacyHighscoreId, Limit: null, Verbose: true), CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Contains("AmbiguousOrOffsetless", writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private async Task<(Guid AccountId, Guid PlayerId)> SeedLinkedAccountAsync(int legacyUserId)
    {
        await using var db = v2Fixture.CreateDbContext();
        return await TestSeeding.SeedLinkedAccountAsync(db, legacyUserId, "audit");
    }
}
