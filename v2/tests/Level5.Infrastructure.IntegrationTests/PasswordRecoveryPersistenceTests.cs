using Level5.Application.Common;
using Level5.Domain.Identity;
using Level5.Infrastructure.Identity;
using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class PasswordRecoveryPersistenceTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Challenge_constraints_and_hash_only_persistence_are_enforced()
    {
        var account = await SeedVerifiedAccountAsync();
        var generator = new PasswordResetTokenGenerator();
        var token = generator.Generate();

        await using var db = fixture.CreateDbContext();
        var store = new PasswordResetChallengeStore(db);
        await store.AddAsync(PasswordResetChallenge.Create(account.Id, account.Email!, token.Hash, Now, TimeSpan.FromHours(1)), default);
        await db.SaveChangesAsync();

        var row = await db.PasswordResetChallenges.AsNoTracking().SingleAsync(c => c.AccountId == account.Id.Value);
        Assert.Equal(token.Hash, row.TokenHash);
        Assert.DoesNotContain(token.RawValue, row.TokenHash, StringComparison.Ordinal);

        await store.AddAsync(PasswordResetChallenge.Create(account.Id, account.Email!, generator.Generate().Hash, Now, TimeSpan.FromHours(1)), default);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Concurrent_credential_changes_have_exactly_one_generation_winner()
    {
        var seeded = await SeedVerifiedAccountAsync();
        await using var firstDb = fixture.CreateDbContext();
        await using var secondDb = fixture.CreateDbContext();
        var firstStore = new AccountStore(firstDb);
        var secondStore = new AccountStore(secondDb);
        var first = await firstStore.FindByIdAsync(seeded.Id, default);
        var second = await secondStore.FindByIdAsync(seeded.Id, default);

        first!.ChangePassword("winner");
        second!.ChangePassword("loser");
        await firstStore.StageCredentialUpdateAsync(first, 0, default);
        await secondStore.StageCredentialUpdateAsync(second, 0, default);
        await firstDb.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync());

        await using var readDb = fixture.CreateDbContext();
        var persisted = await new AccountStore(readDb).FindByIdAsync(seeded.Id, default);
        Assert.Equal("winner", persisted!.PasswordHash);
        Assert.Equal(1, persisted.SessionGeneration);
    }

    [Fact]
    public async Task Reset_account_change_rolls_back_when_challenge_consumption_loses()
    {
        var seeded = await SeedChallengeAsync();
        await using var losingDb = fixture.CreateDbContext();
        var losingAccounts = new AccountStore(losingDb);
        var losingChallenges = new PasswordResetChallengeStore(losingDb);
        var losingAccount = await losingAccounts.FindByIdAsync(seeded.Account.Id, default);
        var losingChallenge = await losingChallenges.FindByAccountIdAsync(seeded.Account.Id, default);

        await using (var winningDb = fixture.CreateDbContext())
        {
            var winnerStore = new PasswordResetChallengeStore(winningDb);
            var winner = await winnerStore.FindByAccountIdAsync(seeded.Account.Id, default);
            winner!.Consume(Now.AddMinutes(1));
            await winnerStore.StageUpdateAsync(winner, default);
            await winningDb.SaveChangesAsync();
        }

        losingAccount!.ChangePassword("must-roll-back");
        losingChallenge!.Consume(Now.AddMinutes(1));
        await losingAccounts.StageCredentialUpdateAsync(losingAccount, 0, default);
        await losingChallenges.StageUpdateAsync(losingChallenge, default);
        await Assert.ThrowsAsync<ConflictException>(() => new EfUnitOfWork(losingDb).SaveChangesAsync(default));

        await using var readDb = fixture.CreateDbContext();
        var persisted = await new AccountStore(readDb).FindByIdAsync(seeded.Account.Id, default);
        Assert.Equal("old-hash", persisted!.PasswordHash);
        Assert.Equal(0, persisted.SessionGeneration);
    }

    [Fact]
    public async Task Transparent_rehash_cannot_overwrite_concurrent_password_change()
    {
        var seeded = await SeedVerifiedAccountAsync();
        await using var rehashDb = fixture.CreateDbContext();
        var rehashStore = new AccountStore(rehashDb);
        var stale = await rehashStore.FindByIdAsync(seeded.Id, default);

        await using (var changeDb = fixture.CreateDbContext())
        {
            var changeStore = new AccountStore(changeDb);
            var changed = await changeStore.FindByIdAsync(seeded.Id, default);
            changed!.ChangePassword("new-password");
            await changeStore.StageCredentialUpdateAsync(changed, 0, default);
            await changeDb.SaveChangesAsync();
        }

        stale!.MaintainPasswordHash("stale-rehash");
        await rehashStore.StageCredentialUpdateAsync(stale, 0, default);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => rehashDb.SaveChangesAsync());
    }

    private async Task<Account> SeedVerifiedAccountAsync()
    {
        var account = Account.Rehydrate(Level5.Domain.Ids.AccountId.New(), Username.Create($"p{Guid.NewGuid():N}"[..15]),
            Email.Create($"p{Guid.NewGuid():N}@example.com"), AccountStatus.Active, "old-hash", Now, Now);
        await using var db = fixture.CreateDbContext();
        await new AccountStore(db).AddAsync(account, default);
        await db.SaveChangesAsync();
        return account;
    }

    private async Task<(Account Account, PasswordResetChallenge Challenge)> SeedChallengeAsync()
    {
        var account = await SeedVerifiedAccountAsync();
        var challenge = PasswordResetChallenge.Create(account.Id, account.Email!, $"h{Guid.NewGuid():N}", Now, TimeSpan.FromHours(1));
        await using var db = fixture.CreateDbContext();
        await new PasswordResetChallengeStore(db).AddAsync(challenge, default);
        await db.SaveChangesAsync();
        return (account, challenge);
    }
}
