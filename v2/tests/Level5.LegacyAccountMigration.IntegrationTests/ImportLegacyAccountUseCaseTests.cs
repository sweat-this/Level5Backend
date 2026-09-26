using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Migration;
using Level5.Application.Players;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Domain.Migration;
using Level5.Domain.Players;
using Level5.Infrastructure.Identity;
using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;
using Level5.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;

namespace Level5.LegacyAccountMigration.IntegrationTests;

[Collection(V2PostgresCollection.Name)]
public sealed class ImportLegacyAccountUseCaseTests(V2PostgresFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private static readonly AspNetPasswordHasher Hasher = new();

    private static ImportLegacyAccountUseCase CreateUseCase(Level5V2DbContext db)
        => new(
            new AccountStore(db),
            new PlayerProfileStore(db),
            new LegacyAccountLinkStore(db),
            new PlayerTagAllocator(new PlayerProfileStore(db)),
            new EfUnitOfWork(db),
            Clock,
            Hasher);

    private static string UniqueUsername(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..15];

    [Fact]
    public async Task Fresh_row_with_recognized_hash_is_imported_atomically()
    {
        await using var db = fixture.CreateDbContext();
        var useCase = CreateUseCase(db);
        var username = UniqueUsername("fresh");

        var result = await useCase.ExecuteAsync(
            new ImportLegacyAccountRequest(LegacyUserId: 100_001, username, LegacyCredential.AlreadyHashed("some-hash"),username),
            CancellationToken.None);

        Assert.Equal(ImportLegacyAccountOutcome.Imported, result.Outcome);
        Assert.NotNull(result.AccountId);
        Assert.NotNull(result.PlayerId);

        await using var readDb = fixture.CreateDbContext();
        var account = await new AccountStore(readDb).FindByIdAsync(result.AccountId!.Value, CancellationToken.None);
        var profile = await new PlayerProfileStore(readDb).FindByIdAsync(result.PlayerId!.Value, CancellationToken.None);
        var link = await new LegacyAccountLinkStore(readDb).FindByLegacyUserIdAsync(100_001, CancellationToken.None);

        Assert.NotNull(account);
        Assert.Equal(AccountStatus.Active, account!.Status);
        Assert.NotNull(profile);
        Assert.Equal(account.Id, profile!.AccountId);
        Assert.NotNull(link);
        Assert.Equal(account.Id, link!.AccountId);
        Assert.Equal(profile.Id, link.PlayerId);
    }

    [Fact]
    public async Task Rerunning_an_already_imported_row_is_idempotent_and_writes_nothing_new()
    {
        await using var db = fixture.CreateDbContext();
        var useCase = CreateUseCase(db);
        var username = UniqueUsername("idem");
        var request = new ImportLegacyAccountRequest(LegacyUserId: 100_002, username, LegacyCredential.AlreadyHashed("some-hash"),username);

        var first = await useCase.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(ImportLegacyAccountOutcome.Imported, first.Outcome);

        await using var db2 = fixture.CreateDbContext();
        var accountCountBefore = await db2.Accounts.CountAsync();
        var profileCountBefore = await db2.PlayerProfiles.CountAsync();
        var linkCountBefore = await db2.LegacyAccountLinks.CountAsync();

        var second = await CreateUseCase(db2).ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ImportLegacyAccountOutcome.AlreadyLinkedConsistent, second.Outcome);
        Assert.Equal(first.AccountId, second.AccountId);
        Assert.Equal(first.PlayerId, second.PlayerId);

        await using var db3 = fixture.CreateDbContext();
        Assert.Equal(accountCountBefore, await db3.Accounts.CountAsync());
        Assert.Equal(profileCountBefore, await db3.PlayerProfiles.CountAsync());
        Assert.Equal(linkCountBefore, await db3.LegacyAccountLinks.CountAsync());
    }

    [Fact]
    public async Task Existing_V2_username_with_no_link_blocks_rather_than_auto_linking()
    {
        var username = UniqueUsername("taken");

        await using (var seedDb = fixture.CreateDbContext())
        {
            await new AccountStore(seedDb).AddAsync(Account.Register(Username.Create(username), "existing-hash", Clock.UtcNow), CancellationToken.None);
            await seedDb.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        var result = await CreateUseCase(db).ExecuteAsync(
            new ImportLegacyAccountRequest(LegacyUserId: 100_003, username, LegacyCredential.AlreadyHashed("some-hash"),username),
            CancellationToken.None);

        Assert.Equal(ImportLegacyAccountOutcome.Blocked, result.Outcome);
        Assert.Contains("Refusing to auto-link", result.BlockReason);

        await using var readDb = fixture.CreateDbContext();
        Assert.Null(await new LegacyAccountLinkStore(readDb).FindByLegacyUserIdAsync(100_003, CancellationToken.None));
    }

    [Fact]
    public async Task Collision_with_an_already_migrated_account_gets_a_distinct_message()
    {
        // V1 usernames were case-sensitive; V2's are case-insensitive (Username.Canonical), so two
        // distinct legitimate V1 users differing only by case will always collide here. Both are
        // correctly blocked either way, but the operator-facing reason should say which situation
        // they're looking at.
        var baseUsername = UniqueUsername("casevariant");

        await using (var firstDb = fixture.CreateDbContext())
        {
            var firstResult = await CreateUseCase(firstDb).ExecuteAsync(
                new ImportLegacyAccountRequest(300_001, baseUsername.ToLowerInvariant(), LegacyCredential.AlreadyHashed("hash-1"), baseUsername.ToLowerInvariant()),
                CancellationToken.None);
            Assert.Equal(ImportLegacyAccountOutcome.Imported, firstResult.Outcome);
        }

        await using var db = fixture.CreateDbContext();
        var secondResult = await CreateUseCase(db).ExecuteAsync(
            new ImportLegacyAccountRequest(300_002, baseUsername.ToUpperInvariant(), LegacyCredential.AlreadyHashed("hash-2"), baseUsername.ToUpperInvariant()),
            CancellationToken.None);

        Assert.Equal(ImportLegacyAccountOutcome.Blocked, secondResult.Outcome);
        Assert.Contains("migrated there from a different legacy user", secondResult.BlockReason);
    }

    [Fact]
    public async Task Valid_nonconflicting_email_is_preserved()
    {
        await using var db = fixture.CreateDbContext();
        var username = UniqueUsername("withmail");
        var email = $"{username}@example.com";

        var result = await CreateUseCase(db).ExecuteAsync(
            new ImportLegacyAccountRequest(200_001, username, LegacyCredential.AlreadyHashed("some-hash"),username, LegacyEmail: email),
            CancellationToken.None);

        Assert.Equal(ImportLegacyAccountOutcome.Imported, result.Outcome);

        await using var readDb = fixture.CreateDbContext();
        var account = await new AccountStore(readDb).FindByIdAsync(result.AccountId!.Value, CancellationToken.None);
        Assert.NotNull(account!.Email);
        Assert.Equal(email, account.Email!.Value);
    }

    [Fact]
    public async Task Invalid_email_blocks_by_default_but_can_be_omitted_with_explicit_optin()
    {
        await using var db = fixture.CreateDbContext();
        var username = UniqueUsername("bademail");

        var blockedResult = await CreateUseCase(db).ExecuteAsync(
            new ImportLegacyAccountRequest(200_002, username, LegacyCredential.AlreadyHashed("some-hash"),username, LegacyEmail: "not-an-email"),
            CancellationToken.None);

        Assert.Equal(ImportLegacyAccountOutcome.Blocked, blockedResult.Outcome);
        Assert.Contains("not a valid V2 email", blockedResult.BlockReason);

        var omittedResult = await CreateUseCase(db).ExecuteAsync(
            new ImportLegacyAccountRequest(200_002, username, LegacyCredential.AlreadyHashed("some-hash"),username, LegacyEmail: "not-an-email", OmitInvalidOrConflictingEmail: true),
            CancellationToken.None);

        Assert.Equal(ImportLegacyAccountOutcome.Imported, omittedResult.Outcome);

        await using var readDb = fixture.CreateDbContext();
        var account = await new AccountStore(readDb).FindByIdAsync(omittedResult.AccountId!.Value, CancellationToken.None);
        Assert.Null(account!.Email);
    }

    [Fact]
    public async Task Conflicting_email_blocks_by_default_but_can_be_omitted_with_explicit_optin()
    {
        var sharedEmail = $"shared{Guid.NewGuid():N}@example.com";

        await using (var seedDb = fixture.CreateDbContext())
        {
            await new AccountStore(seedDb).AddAsync(
                Account.Register(Username.Create(UniqueUsername("owner")), "hash", Clock.UtcNow, Email.Create(sharedEmail)),
                CancellationToken.None);
            await seedDb.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        var username = UniqueUsername("dupemail");

        var blockedResult = await CreateUseCase(db).ExecuteAsync(
            new ImportLegacyAccountRequest(200_003, username, LegacyCredential.AlreadyHashed("some-hash"),username, LegacyEmail: sharedEmail.ToUpperInvariant()),
            CancellationToken.None);

        Assert.Equal(ImportLegacyAccountOutcome.Blocked, blockedResult.Outcome);
        Assert.Contains("canonical conflict", blockedResult.BlockReason);

        var omittedResult = await CreateUseCase(db).ExecuteAsync(
            new ImportLegacyAccountRequest(200_003, username, LegacyCredential.AlreadyHashed("some-hash"),username, LegacyEmail: sharedEmail, OmitInvalidOrConflictingEmail: true),
            CancellationToken.None);

        Assert.Equal(ImportLegacyAccountOutcome.Imported, omittedResult.Outcome);

        await using var readDb = fixture.CreateDbContext();
        var account = await new AccountStore(readDb).FindByIdAsync(omittedResult.AccountId!.Value, CancellationToken.None);
        Assert.Null(account!.Email);
    }

    [Fact]
    public async Task Invalid_legacy_username_is_blocked_not_thrown()
    {
        await using var db = fixture.CreateDbContext();

        var result = await CreateUseCase(db).ExecuteAsync(
            new ImportLegacyAccountRequest(LegacyUserId: 100_004, "a b", LegacyCredential.AlreadyHashed("some-hash"),"a b"), // space is outside Username's grammar
            CancellationToken.None);

        Assert.Equal(ImportLegacyAccountOutcome.Blocked, result.Outcome);
        Assert.Contains("not a valid V2 username", result.BlockReason);

        await using var readDb = fixture.CreateDbContext();
        Assert.Null(await new LegacyAccountLinkStore(readDb).FindByLegacyUserIdAsync(100_004, CancellationToken.None));
    }

    [Fact]
    public async Task Mismatched_link_pairing_is_detected_as_inconsistent_on_resume()
    {
        // The FK design (legacy_account_links.AccountId/PlayerId both reference real rows,
        // Restrict on delete) makes a link pointing at a MISSING account or profile structurally
        // impossible to construct - that's by design (see LegacyLinkConsistencyChecker's
        // comments). The one inconsistency the FKs cannot prevent is a link whose AccountId and
        // PlayerId each reference real, independently valid rows that simply don't belong to each
        // other - exactly what this test constructs.
        await using var seedDb = fixture.CreateDbContext();
        var now = Clock.UtcNow;

        var accountA = Account.Register(Username.Create(UniqueUsername("acca")), "hash-a", now);
        var profileA = PlayerProfile.Create(accountA.Id, "ProfileA", PlayerTag.Create($"PROFILEA#{Random.Shared.Next(1000, 9999)}"), now);
        var accountB = Account.Register(Username.Create(UniqueUsername("accb")), "hash-b", now);
        var profileB = PlayerProfile.Create(accountB.Id, "ProfileB", PlayerTag.Create($"PROFILEB#{Random.Shared.Next(1000, 9999)}"), now);

        await new AccountStore(seedDb).AddAsync(accountA, CancellationToken.None);
        await new AccountStore(seedDb).AddAsync(accountB, CancellationToken.None);
        await new PlayerProfileStore(seedDb).AddAsync(profileA, CancellationToken.None);
        await new PlayerProfileStore(seedDb).AddAsync(profileB, CancellationToken.None);
        // Deliberately mismatched: AccountId from A, PlayerId from B.
        await new LegacyAccountLinkStore(seedDb).AddAsync(
            new LegacyAccountLink(100_005, accountA.Id, profileB.Id, "mismatched", now), CancellationToken.None);
        await seedDb.SaveChangesAsync();

        await using var db = fixture.CreateDbContext();
        await Assert.ThrowsAsync<LegacyMigrationInconsistentException>(() =>
            CreateUseCase(db).ExecuteAsync(new ImportLegacyAccountRequest(100_005, "mismatched", LegacyCredential.AlreadyHashed("some-hash"),"mismatched"), CancellationToken.None));
    }

    [Fact]
    public async Task Tag_exhaustion_propagates_and_is_not_swallowed_as_blocked()
    {
        await using var db = fixture.CreateDbContext();
        var username = UniqueUsername("exhaust");

        var useCase = new ImportLegacyAccountUseCase(
            new AccountStore(db),
            new PlayerProfileStore(db),
            new LegacyAccountLinkStore(db),
            new PlayerTagAllocator(new AlwaysCollidingPlayerProfileStore()),
            new EfUnitOfWork(db),
            Clock,
            Hasher);

        await Assert.ThrowsAsync<ConflictException>(() =>
            useCase.ExecuteAsync(new ImportLegacyAccountRequest(100_006, username, LegacyCredential.AlreadyHashed("some-hash"),username), CancellationToken.None));
    }

    [Fact]
    public void LegacyCredential_ToString_never_prints_the_raw_value()
    {
        // Guards against the record's default ToString()/PrintMembers, which would otherwise print
        // Value verbatim - the one thing this feature must never do, per its own stated invariant
        // (and the original spec's required "credential material is absent from logs/reports").
        const string secret = "super-secret-legacy-plaintext";

        var printed = LegacyCredential.Plaintext(secret).ToString();

        Assert.DoesNotContain(secret, printed);
    }
}
