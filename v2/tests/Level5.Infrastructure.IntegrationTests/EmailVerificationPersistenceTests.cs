using Level5.Domain.Identity;
using Level5.Infrastructure.Persistence.Repositories;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class EmailVerificationPersistenceTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Challenge_round_trips_without_persisting_the_raw_token()
    {
        var rawToken = $"raw-{Guid.NewGuid():N}";
        var tokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(rawToken)));

        await using var db = fixture.CreateDbContext();
        var account = Account.Register(Username.Create($"u{Guid.NewGuid():N}"[..15]), "hash", Now);
        await new AccountStore(db).AddAsync(account, CancellationToken.None);
        var challenge = EmailVerificationChallenge.Create(
            account.Id, Email.Create($"e{Guid.NewGuid():N}@example.com"), tokenHash, Now, TimeSpan.FromHours(24));
        await new EmailVerificationChallengeStore(db).AddAsync(challenge, CancellationToken.None);
        await db.SaveChangesAsync();

        var row = await db.EmailVerificationChallenges.AsNoTracking().SingleAsync(c => c.Id == challenge.Id.Value);
        Assert.Equal(tokenHash, row.TokenHash);
        Assert.DoesNotContain(rawToken, row.TokenHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Database_enforces_one_challenge_per_account_and_unique_token_hash()
    {
        await using var db = fixture.CreateDbContext();
        var account = Account.Register(Username.Create($"u{Guid.NewGuid():N}"[..15]), "hash", Now);
        var other = Account.Register(Username.Create($"u{Guid.NewGuid():N}"[..15]), "hash", Now);
        var accounts = new AccountStore(db);
        await accounts.AddAsync(account, CancellationToken.None);
        await accounts.AddAsync(other, CancellationToken.None);
        await db.SaveChangesAsync();

        var store = new EmailVerificationChallengeStore(db);
        await store.AddAsync(Create(account, "hash-one"), CancellationToken.None);
        await db.SaveChangesAsync();

        await store.AddAsync(Create(account, "hash-two"), CancellationToken.None);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        await store.AddAsync(Create(other, "hash-one"), CancellationToken.None);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Database_reserves_target_email_canonical_across_accounts()
    {
        await using var db = fixture.CreateDbContext();
        var first = Account.Register(Username.Create($"u{Guid.NewGuid():N}"[..15]), "hash", Now);
        var second = Account.Register(Username.Create($"u{Guid.NewGuid():N}"[..15]), "hash", Now);
        var accounts = new AccountStore(db);
        await accounts.AddAsync(first, CancellationToken.None);
        await accounts.AddAsync(second, CancellationToken.None);
        await db.SaveChangesAsync();

        var target = Email.Create($"reserved{Guid.NewGuid():N}@example.com");
        var store = new EmailVerificationChallengeStore(db);
        await store.AddAsync(
            EmailVerificationChallenge.Create(
                first.Id, target, $"hash-{Guid.NewGuid():N}", Now, TimeSpan.FromHours(24)),
            CancellationToken.None);
        await db.SaveChangesAsync();

        await store.AddAsync(
            EmailVerificationChallenge.Create(
                second.Id,
                Email.Create(target.Value.ToUpperInvariant()),
                $"hash-{Guid.NewGuid():N}",
                Now,
                TimeSpan.FromHours(24)),
            CancellationToken.None);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Consumed_challenge_releases_target_email_reservation()
    {
        await using var db = fixture.CreateDbContext();
        var first = Account.Register(Username.Create($"u{Guid.NewGuid():N}"[..15]), "hash", Now);
        var second = Account.Register(Username.Create($"u{Guid.NewGuid():N}"[..15]), "hash", Now);
        var accounts = new AccountStore(db);
        await accounts.AddAsync(first, CancellationToken.None);
        await accounts.AddAsync(second, CancellationToken.None);
        await db.SaveChangesAsync();

        var target = Email.Create($"released{Guid.NewGuid():N}@example.com");
        var store = new EmailVerificationChallengeStore(db);
        var original = EmailVerificationChallenge.Create(
            first.Id, target, $"hash-{Guid.NewGuid():N}", Now, TimeSpan.FromHours(24));
        await store.AddAsync(original, CancellationToken.None);
        await db.SaveChangesAsync();

        original.Consume(Now.AddMinutes(1));
        await store.StageUpdateAsync(original, CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await store.AddAsync(
            EmailVerificationChallenge.Create(
                second.Id, target, $"hash-{Guid.NewGuid():N}", Now.AddMinutes(2), TimeSpan.FromHours(24)),
            CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(
            2,
            await db.EmailVerificationChallenges.CountAsync(
                challenge => challenge.TargetEmailCanonical == target.Canonical));
    }

    [Fact]
    public async Task Concurrent_rotations_have_exactly_one_revision_winner()
    {
        var account = await SeedAsync("rotate");

        await using var firstDb = fixture.CreateDbContext();
        await using var secondDb = fixture.CreateDbContext();
        var firstStore = new EmailVerificationChallengeStore(firstDb);
        var secondStore = new EmailVerificationChallengeStore(secondDb);
        var first = await firstStore.FindByAccountIdAsync(account.Id, CancellationToken.None);
        var second = await secondStore.FindByAccountIdAsync(account.Id, CancellationToken.None);

        var firstTarget = Email.Create($"first{Guid.NewGuid():N}@example.com");
        var secondTarget = Email.Create($"second{Guid.NewGuid():N}@example.com");
        first!.Rotate(firstTarget, "rotate-one", Now.AddMinutes(10), TimeSpan.FromHours(24));
        second!.Rotate(secondTarget, "rotate-two", Now.AddMinutes(10), TimeSpan.FromHours(24));
        await firstStore.StageUpdateAsync(first, CancellationToken.None);
        await secondStore.StageUpdateAsync(second, CancellationToken.None);

        await firstDb.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync());

        await using var readDb = fixture.CreateDbContext();
        var persisted = await new EmailVerificationChallengeStore(readDb)
            .FindByAccountIdAsync(account.Id, CancellationToken.None);
        Assert.Equal("rotate-one", persisted!.TokenHash);
        Assert.Equal(firstTarget, persisted.TargetEmail);
        Assert.Equal(1, persisted.Revision);
    }

    [Fact]
    public async Task Concurrent_consumption_has_exactly_one_revision_winner()
    {
        var account = await SeedAsync("consume");

        await using var firstDb = fixture.CreateDbContext();
        await using var secondDb = fixture.CreateDbContext();
        var firstStore = new EmailVerificationChallengeStore(firstDb);
        var secondStore = new EmailVerificationChallengeStore(secondDb);
        var first = await firstStore.FindByAccountIdAsync(account.Id, CancellationToken.None);
        var second = await secondStore.FindByAccountIdAsync(account.Id, CancellationToken.None);

        first!.Consume(Now.AddMinutes(1));
        second!.Consume(Now.AddMinutes(1));
        await firstStore.StageUpdateAsync(first, CancellationToken.None);
        await secondStore.StageUpdateAsync(second, CancellationToken.None);

        await firstDb.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync());
    }

    [Fact]
    public async Task Account_verification_and_challenge_consumption_commit_atomically()
    {
        var account = await SeedAsync("atomic");

        await using (var failingDb = fixture.CreateDbContext())
        {
            var accountStore = new AccountStore(failingDb);
            var challengeStore = new EmailVerificationChallengeStore(failingDb);
            var loadedAccount = await accountStore.FindByIdAsync(account.Id, CancellationToken.None);
            var challenge = await challengeStore.FindByAccountIdAsync(account.Id, CancellationToken.None);
            loadedAccount!.VerifyEmail(challenge!.TargetEmail, Now.AddMinutes(1));
            challenge.Consume(Now.AddMinutes(1));
            await accountStore.StageEmailUpdateAsync(loadedAccount, CancellationToken.None);
            await challengeStore.StageUpdateAsync(challenge, CancellationToken.None);

            // Force the same SaveChanges transaction to fail after both legitimate changes were
            // staged. PostgreSQL must roll the account and challenge changes back together.
            failingDb.EmailVerificationChallenges.Add(new EmailVerificationChallengeRow
            {
                Id = Guid.CreateVersion7(),
                AccountId = account.Id.Value,
                TargetEmail = challenge.TargetEmail.Value,
                TargetEmailCanonical = challenge.TargetEmail.Canonical,
                TokenHash = $"other-{Guid.NewGuid():N}",
                IssuedAt = Now,
                ExpiresAt = Now.AddHours(1),
                Revision = 0
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => failingDb.SaveChangesAsync());
        }

        await using var readDb = fixture.CreateDbContext();
        var persistedAccount = await new AccountStore(readDb).FindByIdAsync(account.Id, CancellationToken.None);
        var persistedChallenge = await new EmailVerificationChallengeStore(readDb)
            .FindByAccountIdAsync(account.Id, CancellationToken.None);
        Assert.Null(persistedAccount!.EmailVerifiedAt);
        Assert.Null(persistedChallenge!.ConsumedAt);
        Assert.Equal(0, persistedChallenge.Revision);
    }

    [Fact]
    public async Task Email_replacement_promotion_and_challenge_consumption_commit_atomically()
    {
        var currentEmail = Email.Create($"current{Guid.NewGuid():N}@example.com");
        var replacement = Email.Create($"replacement{Guid.NewGuid():N}@example.com");
        Account account;
        await using (var seedDb = fixture.CreateDbContext())
        {
            account = Account.Register(
                Username.Create($"u{Guid.NewGuid():N}"[..15]),
                "hash",
                Now,
                currentEmail);
            account.VerifyEmail(currentEmail, Now);
            await new AccountStore(seedDb).AddAsync(account, CancellationToken.None);
            await new EmailVerificationChallengeStore(seedDb).AddAsync(
                EmailVerificationChallenge.Create(
                    account.Id,
                    replacement,
                    $"hash-{Guid.NewGuid():N}",
                    Now,
                    TimeSpan.FromHours(24)),
                CancellationToken.None);
            await seedDb.SaveChangesAsync();
        }

        await using (var failingDb = fixture.CreateDbContext())
        {
            var accountStore = new AccountStore(failingDb);
            var challengeStore = new EmailVerificationChallengeStore(failingDb);
            var loadedAccount = await accountStore.FindByIdAsync(account.Id, CancellationToken.None);
            var challenge = await challengeStore.FindByAccountIdAsync(account.Id, CancellationToken.None);
            loadedAccount!.PromoteVerifiedEmail(replacement, Now.AddMinutes(1));
            challenge!.Consume(Now.AddMinutes(1));
            await accountStore.StageEmailUpdateAsync(loadedAccount, CancellationToken.None);
            await challengeStore.StageUpdateAsync(challenge, CancellationToken.None);

            // The duplicate AccountId makes the unit-of-work fail after both legitimate updates
            // are staged. PostgreSQL must roll promotion and consumption back together.
            failingDb.EmailVerificationChallenges.Add(new EmailVerificationChallengeRow
            {
                Id = Guid.CreateVersion7(),
                AccountId = account.Id.Value,
                TargetEmail = replacement.Value,
                TargetEmailCanonical = replacement.Canonical,
                TokenHash = $"other-{Guid.NewGuid():N}",
                IssuedAt = Now,
                ExpiresAt = Now.AddHours(1),
                Revision = 0
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => failingDb.SaveChangesAsync());
        }

        await using var readDb = fixture.CreateDbContext();
        var persistedAccount = await new AccountStore(readDb)
            .FindByIdAsync(account.Id, CancellationToken.None);
        var persistedChallenge = await new EmailVerificationChallengeStore(readDb)
            .FindByAccountIdAsync(account.Id, CancellationToken.None);
        Assert.Equal(currentEmail, persistedAccount!.Email);
        Assert.NotNull(persistedAccount.EmailVerifiedAt);
        Assert.Equal(Now, persistedAccount.EmailVerifiedAt.Value, TimeSpan.FromMilliseconds(1));
        Assert.Null(persistedChallenge!.ConsumedAt);
        Assert.Equal(0, persistedChallenge.Revision);
    }

    private async Task<Account> SeedAsync(string prefix)
    {
        await using var db = fixture.CreateDbContext();
        var account = Account.Register(
            Username.Create($"{prefix}{Guid.NewGuid():N}"[..15]),
            "hash",
            Now,
            Email.Create($"{prefix}{Guid.NewGuid():N}@example.com"));
        await new AccountStore(db).AddAsync(account, CancellationToken.None);
        await new EmailVerificationChallengeStore(db).AddAsync(
            Create(account, $"seed-{Guid.NewGuid():N}"), CancellationToken.None);
        await db.SaveChangesAsync();
        return account;
    }

    private static EmailVerificationChallenge Create(Account account, string hash)
        => EmailVerificationChallenge.Create(
            account.Id,
            account.Email ?? Email.Create($"fallback{Guid.NewGuid():N}@example.com"),
            hash,
            Now,
            TimeSpan.FromHours(24));
}
