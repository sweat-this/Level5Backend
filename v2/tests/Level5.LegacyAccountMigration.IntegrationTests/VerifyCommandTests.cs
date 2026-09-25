using Level5.Infrastructure.Identity;
using Level5.LegacyAccountMigration.Commands;
using Level5.LegacyAccountMigration.Legacy;
using Microsoft.EntityFrameworkCore;

namespace Level5.LegacyAccountMigration.IntegrationTests;

[Collection(MigrationTestCollection.Name)]
public sealed class VerifyCommandTests(LegacyPostgresFixture legacyFixture, V2PostgresFixture v2Fixture)
{
    private static readonly AspNetPasswordHasher Hasher = new();

    [Fact]
    public async Task Unmigrated_row_reports_not_migrated_with_zero_exit()
    {
        var username = Unique("verifyunmigrated");
        var legacyUserId = await legacyFixture.InsertUserAsync(username, Hasher.Hash("whatever"));

        await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
        var exitCode = await VerifyCommand.RunAsync(
            new LegacyUserReader(legacyFixture.ConnectionString), provider, new VerifyOptions(legacyUserId), CancellationToken.None);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Cleanly_migrated_row_verifies_as_consistent()
    {
        var username = Unique("verifyclean");
        var legacyUserId = await legacyFixture.InsertUserAsync(username, Hasher.Hash("whatever"));

        await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
        var reader = new LegacyUserReader(legacyFixture.ConnectionString);

        var migrateExit = await MigrateCommand.RunAsync(reader, provider, new MigrateOptions(false, legacyUserId, null), CancellationToken.None);
        Assert.Equal(0, migrateExit);

        var verifyExit = await VerifyCommand.RunAsync(reader, provider, new VerifyOptions(legacyUserId), CancellationToken.None);
        Assert.Equal(0, verifyExit);
    }

    [Fact]
    public async Task Corrupted_link_row_verifies_as_inconsistent_with_nonzero_exit()
    {
        var usernameA = Unique("verifycorrupta");
        var usernameB = Unique("verifycorruptb");
        var legacyUserIdA = await legacyFixture.InsertUserAsync(usernameA, Hasher.Hash("whatever"));
        var legacyUserIdB = await legacyFixture.InsertUserAsync(usernameB, Hasher.Hash("whatever"));

        await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
        var reader = new LegacyUserReader(legacyFixture.ConnectionString);

        await MigrateCommand.RunAsync(reader, provider, new MigrateOptions(false, legacyUserIdA, null), CancellationToken.None);
        await MigrateCommand.RunAsync(reader, provider, new MigrateOptions(false, legacyUserIdB, null), CancellationToken.None);

        // Corrupt A's link to point at B's PlayerProfile while keeping A's own AccountId - hand
        // editing outside the tool, exactly the scenario verify exists to catch. Both FKs stay
        // satisfied (B's profile is a real row), so this bypasses the database's own
        // referential-integrity guard, which is why application-level verification is still
        // necessary. B's own link row is deleted first so the unique index on PlayerId (which
        // would otherwise forbid two links referencing the same profile) doesn't block the edit -
        // a link row has no FK pointing into it, so deleting one is otherwise unconstrained.
        await using (var corruptDb = v2Fixture.CreateDbContext())
        {
            var linkB = await corruptDb.LegacyAccountLinks.SingleAsync(l => l.LegacyUserId == legacyUserIdB);
            var profileBId = linkB.PlayerId;
            corruptDb.LegacyAccountLinks.Remove(linkB);
            await corruptDb.SaveChangesAsync();

            var linkA = await corruptDb.LegacyAccountLinks.SingleAsync(l => l.LegacyUserId == legacyUserIdA);
            linkA.PlayerId = profileBId;
            await corruptDb.SaveChangesAsync();
        }

        var exitCode = await VerifyCommand.RunAsync(reader, provider, new VerifyOptions(LegacyUserId: null), CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20];
}
