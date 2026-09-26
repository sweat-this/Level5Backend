using Level5.Infrastructure.Identity;
using Level5.LegacyAccountMigration.Commands;
using Level5.LegacyAccountMigration.Legacy;
using Microsoft.EntityFrameworkCore;

namespace Level5.LegacyAccountMigration.IntegrationTests;

[Collection(MigrationTestCollection.Name)]
public sealed class AuditCommandTests(LegacyPostgresFixture legacyFixture, V2PostgresFixture v2Fixture)
{
    private static readonly AspNetPasswordHasher Hasher = new();

    [Fact]
    public async Task Audit_never_writes_to_v2_regardless_of_findings()
    {
        var cleanUsername = Unique("auditclean");
        var plaintextUsername = Unique("auditplain");
        await legacyFixture.InsertUserAsync(cleanUsername, Hasher.Hash("whatever"));
        await legacyFixture.InsertUserAsync(plaintextUsername, "raw-plaintext-value");

        await using var db = v2Fixture.CreateDbContext();
        var accountCountBefore = await db.Accounts.CountAsync();
        var profileCountBefore = await db.PlayerProfiles.CountAsync();
        var linkCountBefore = await db.LegacyAccountLinks.CountAsync();

        await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
        var exitCode = await AuditCommand.RunAsync(
            new LegacyUserReader(legacyFixture.ConnectionString), provider, new AuditOptions(LegacyUserId: null, Limit: null, Verbose: true), CancellationToken.None);

        // The audited set is a superset of every row inserted by earlier tests in this shared
        // collection (audit has no --legacy-user-id filter here), but the exit-code contract only
        // depends on whether ANY row is blocked, which prior tests' rows may already trigger - so
        // this assertion is about writes, not the exit code.
        Assert.True(exitCode is 0 or 1);

        await using var afterDb = v2Fixture.CreateDbContext();
        Assert.Equal(accountCountBefore, await afterDb.Accounts.CountAsync());
        Assert.Equal(profileCountBefore, await afterDb.PlayerProfiles.CountAsync());
        Assert.Equal(linkCountBefore, await afterDb.LegacyAccountLinks.CountAsync());
    }

    [Fact]
    public async Task Audit_reports_a_username_collision_as_blocked_with_nonzero_exit()
    {
        var username = Unique("auditcollide");

        await using (var seedDb = v2Fixture.CreateDbContext())
        {
            await seedDb.Accounts.AddAsync(new Infrastructure.Persistence.Rows.AccountRow
            {
                Id = Guid.NewGuid(),
                Username = username,
                UsernameCanonical = username.ToUpperInvariant(),
                Status = "Active",
                PasswordHash = "existing-hash",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await seedDb.SaveChangesAsync();
        }

        var legacyUserId = await legacyFixture.InsertUserAsync(username, Hasher.Hash("whatever"));

        await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
        var exitCode = await AuditCommand.RunAsync(
            new LegacyUserReader(legacyFixture.ConnectionString), provider, new AuditOptions(legacyUserId, Limit: null, Verbose: false), CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Audit_reports_a_clean_row_with_zero_exit_when_scoped_to_it_alone()
    {
        var username = Unique("auditsolo");
        var legacyUserId = await legacyFixture.InsertUserAsync(username, Hasher.Hash("whatever"));

        await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
        var exitCode = await AuditCommand.RunAsync(
            new LegacyUserReader(legacyFixture.ConnectionString), provider, new AuditOptions(legacyUserId, Limit: null, Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Audit_reports_invalid_email_without_failing_the_exit_code()
    {
        // Unlike a username problem, an invalid/conflicting email has an operator escape hatch
        // (--omit-invalid-email at migrate time), so audit reports it - visible via --verbose -
        // without treating it as a hard blocker the way BlockedInvalidUsername etc. are.
        var username = Unique("auditbademail");
        var legacyUserId = await legacyFixture.InsertUserAsync(username, Hasher.Hash("whatever"), email: "not-an-email");

        var originalOut = Console.Out;
        try
        {
            using var writer = new StringWriter();
            Console.SetOut(writer);

            await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
            var exitCode = await AuditCommand.RunAsync(
                new LegacyUserReader(legacyFixture.ConnectionString), provider, new AuditOptions(legacyUserId, Limit: null, Verbose: true), CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Contains("InvalidEmail", writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task Audit_reports_email_collision_without_failing_the_exit_code()
    {
        // V1's users.email column is varchar(45), so this must stay short.
        var sharedEmail = $"as{Guid.NewGuid():N}"[..20] + "@example.com";
        await using (var seedDb = v2Fixture.CreateDbContext())
        {
            await seedDb.Accounts.AddAsync(new Infrastructure.Persistence.Rows.AccountRow
            {
                Id = Guid.NewGuid(),
                Username = Unique("auditemailowner"),
                UsernameCanonical = Unique("AUDITEMAILOWNER"),
                Email = sharedEmail,
                EmailCanonical = sharedEmail.ToUpperInvariant(),
                Status = "Active",
                PasswordHash = "existing-hash",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await seedDb.SaveChangesAsync();
        }

        var username = Unique("auditdupemail");
        var legacyUserId = await legacyFixture.InsertUserAsync(username, Hasher.Hash("whatever"), email: sharedEmail);

        var originalOut = Console.Out;
        try
        {
            using var writer = new StringWriter();
            Console.SetOut(writer);

            await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
            var exitCode = await AuditCommand.RunAsync(
                new LegacyUserReader(legacyFixture.ConnectionString), provider, new AuditOptions(legacyUserId, Limit: null, Verbose: true), CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Contains("EmailCollision", writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20];
}
