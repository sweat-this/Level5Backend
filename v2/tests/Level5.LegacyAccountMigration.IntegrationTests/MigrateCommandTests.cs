using Level5.Application.Players;
using Level5.Infrastructure.Identity;
using Level5.LegacyAccountMigration.Commands;
using Level5.LegacyAccountMigration.Legacy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Level5.LegacyAccountMigration.IntegrationTests;

/// <summary>
/// Every command call here is scoped to its own row via --legacy-user-id. This is required, not
/// just tidy: LegacyPostgresFixture/V2PostgresFixture are shared across every test class in
/// MigrationTestCollection, and an unfiltered `migrate` walks every V1 row - including a row
/// another test (VerifyCommandTests) deliberately corrupts. Since ImportLegacyAccountUseCase
/// aborts the whole run on the first LegacyMigrationInconsistentException it hits, an unscoped
/// `migrate` call here would nondeterministically abort depending on test execution order. Scoping
/// to one legacy_user_id keeps each test isolated regardless of what other tests in the collection
/// have done.
/// </summary>
[Collection(MigrationTestCollection.Name)]
public sealed class MigrateCommandTests(LegacyPostgresFixture legacyFixture, V2PostgresFixture v2Fixture)
{
    private static readonly AspNetPasswordHasher Hasher = new();

    [Fact]
    public async Task Recognized_hash_row_is_imported_and_authenticates_via_real_login()
    {
        var username = Unique("clean");
        var password = "Correct-Horse-Battery-Staple-1!";
        var recognizedHash = Hasher.Hash(password);
        var legacyUserId = await legacyFixture.InsertUserAsync(username, recognizedHash, $"{username}@example.com");

        await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
        var exitCode = await MigrateCommand.RunAsync(
            new LegacyUserReader(legacyFixture.ConnectionString), provider, new MigrateOptions(AcceptLegacyPlaintext: false, legacyUserId, Limit: null), CancellationToken.None);

        Assert.Equal(0, exitCode);

        await using var db = v2Fixture.CreateDbContext();
        var account = await db.Accounts.SingleAsync(a => a.UsernameCanonical == username.ToUpperInvariant());
        Assert.Equal(recognizedHash, account.PasswordHash);

        // Authenticates through the real hasher exactly as V2 login would - proves the copied
        // hash is not just byte-identical but actually verifies against the original password.
        Assert.Equal(Application.Abstractions.PasswordVerificationResult.Success, Hasher.Verify(account.PasswordHash, password));
    }

    [Fact]
    public async Task Unrecognized_credential_without_flag_is_skipped_and_writes_nothing()
    {
        var username = Unique("plain");
        var legacyUserId = await legacyFixture.InsertUserAsync(username, "plaintext-password-not-a-hash");

        await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
        await MigrateCommand.RunAsync(
            new LegacyUserReader(legacyFixture.ConnectionString), provider, new MigrateOptions(AcceptLegacyPlaintext: false, legacyUserId, Limit: null), CancellationToken.None);

        await using var db = v2Fixture.CreateDbContext();
        Assert.False(await db.Accounts.AnyAsync(a => a.UsernameCanonical == username.ToUpperInvariant()));
    }

    [Fact]
    public async Task Unrecognized_credential_with_accept_flag_persists_only_a_freshly_hashed_value()
    {
        var username = Unique("acceptplain");
        const string rawPlaintext = "hunter2-legacy-plaintext";
        var legacyUserId = await legacyFixture.InsertUserAsync(username, rawPlaintext);

        await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
        await MigrateCommand.RunAsync(
            new LegacyUserReader(legacyFixture.ConnectionString), provider, new MigrateOptions(AcceptLegacyPlaintext: true, legacyUserId, Limit: null), CancellationToken.None);

        await using var db = v2Fixture.CreateDbContext();
        var account = await db.Accounts.SingleAsync(a => a.UsernameCanonical == username.ToUpperInvariant());

        Assert.NotEqual(rawPlaintext, account.PasswordHash);
        Assert.Equal(Application.Abstractions.PasswordVerificationResult.Success, Hasher.Verify(account.PasswordHash, rawPlaintext));
    }

    [Fact]
    public async Task Rerunning_migrate_over_the_same_row_is_idempotent()
    {
        var username = Unique("rerun");
        var legacyUserId = await legacyFixture.InsertUserAsync(username, Hasher.Hash("whatever"));

        await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
        var reader = new LegacyUserReader(legacyFixture.ConnectionString);
        var options = new MigrateOptions(AcceptLegacyPlaintext: false, legacyUserId, Limit: null);

        await MigrateCommand.RunAsync(reader, provider, options, CancellationToken.None);

        await using var db1 = v2Fixture.CreateDbContext();
        var accountCountBefore = await db1.Accounts.CountAsync();

        var secondExitCode = await MigrateCommand.RunAsync(reader, provider, options, CancellationToken.None);

        Assert.Equal(0, secondExitCode);
        await using var db2 = v2Fixture.CreateDbContext();
        Assert.Equal(accountCountBefore, await db2.Accounts.CountAsync());
    }

    [Fact]
    public async Task Username_collision_blocks_without_creating_a_second_account()
    {
        var username = Unique("collide");

        // Pre-existing V2 account with no legacy link.
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
        await MigrateCommand.RunAsync(
            new LegacyUserReader(legacyFixture.ConnectionString), provider, new MigrateOptions(AcceptLegacyPlaintext: false, legacyUserId, Limit: null), CancellationToken.None);

        await using var db = v2Fixture.CreateDbContext();
        Assert.Equal(1, await db.Accounts.CountAsync(a => a.UsernameCanonical == username.ToUpperInvariant()));
    }

    [Fact]
    public async Task Two_legacy_rows_sharing_an_email_the_first_keeps_it_the_second_is_blocked_or_omits_it()
    {
        // V1's users.email has no unique constraint, so two distinct legacy users sharing one
        // email is realistic source data, not a contrived edge case.
        var sharedEmail = $"pair{Guid.NewGuid():N}"[..20] + "@example.com";
        var firstUsername = Unique("emailpairfirst");
        var secondUsername = Unique("emailpairsecond");

        var firstLegacyUserId = await legacyFixture.InsertUserAsync(firstUsername, Hasher.Hash("whatever"), email: sharedEmail);
        var secondLegacyUserId = await legacyFixture.InsertUserAsync(secondUsername, Hasher.Hash("whatever"), email: sharedEmail);

        var reader = new LegacyUserReader(legacyFixture.ConnectionString);

        await using (var firstProvider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString))
        {
            await MigrateCommand.RunAsync(
                reader, firstProvider, new MigrateOptions(false, firstLegacyUserId, null), CancellationToken.None);
        }

        // Without --omit-invalid-email the second row would be blocked entirely (correct, but not
        // what this test is isolating) - the flag proves the "first keeps it, second can't" outcome
        // without conflating it with the separate blocked-vs-imported decision already covered by
        // ImportLegacyAccountUseCaseTests.Conflicting_email_blocks_by_default_but_can_be_omitted_with_explicit_optin.
        await using (var secondProvider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString))
        {
            await MigrateCommand.RunAsync(
                reader, secondProvider, new MigrateOptions(false, secondLegacyUserId, null, OmitInvalidEmail: true), CancellationToken.None);
        }

        await using var db = v2Fixture.CreateDbContext();
        var firstAccount = await db.Accounts.SingleAsync(a => a.UsernameCanonical == firstUsername.ToUpperInvariant());
        var secondAccount = await db.Accounts.SingleAsync(a => a.UsernameCanonical == secondUsername.ToUpperInvariant());

        Assert.Equal(sharedEmail, firstAccount.Email);
        Assert.Null(secondAccount.Email);
    }

    [Fact]
    public async Task Concurrent_write_conflict_is_reported_as_blocked_rather_than_crashing_the_run()
    {
        // Simulates the race the second review pass calls out: something else (in practice, a
        // concurrent live registration - see the README's documented rollout order, which runs
        // migrate before public registration is enabled) wins a unique-index race after this
        // row's own pre-checks passed. Here the race is stood in for deterministically via a
        // rigged PlayerTagAllocator that always reports its candidate tag taken, forcing the same
        // ConflictException a real DB-level unique violation would raise, without needing genuine
        // concurrency. What's under test is that MigrateCommand.RunAsync catches it, reports the
        // row Blocked, and returns normally (0) instead of the exception propagating out of the
        // whole run.
        var username = Unique("raced");
        var legacyUserId = await legacyFixture.InsertUserAsync(username, Hasher.Hash("whatever"));

        await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString,
            services => services.AddScoped<PlayerTagAllocator>(_ => new PlayerTagAllocator(new AlwaysCollidingPlayerProfileStore())));

        var exitCode = await MigrateCommand.RunAsync(
            new LegacyUserReader(legacyFixture.ConnectionString), provider, new MigrateOptions(false, legacyUserId, null), CancellationToken.None);

        Assert.Equal(0, exitCode);

        await using var db = v2Fixture.CreateDbContext();
        Assert.False(await db.Accounts.AnyAsync(a => a.UsernameCanonical == username.ToUpperInvariant()));
    }

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20];
}
