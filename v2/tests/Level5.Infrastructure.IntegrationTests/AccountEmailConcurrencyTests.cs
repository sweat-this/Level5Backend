using Level5.Domain.Identity;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class AccountEmailConcurrencyTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Password_rotation_rejects_stale_email_and_rolls_back_its_challenge(bool rotateAfterStaging)
    {
        var account = await Seed();
        await using var emailDb = fixture.CreateDbContext();
        var emailStore = new AccountStore(emailDb);
        var validated = (await emailStore.FindByIdAsync(account.Id, default))!;
        validated.AttachOrReplaceUnverifiedEmail(Email.Create($"race-{Guid.NewGuid():N}@example.com"));
        await new EmailVerificationChallengeStore(emailDb).AddAsync(EmailVerificationChallenge.Create(
            account.Id, validated.Email!, Guid.NewGuid().ToString("N"), Now, TimeSpan.FromHours(1)), default);
        if (rotateAfterStaging) await emailStore.StageEmailUpdateAsync(validated, default);
        await RotatePassword(account);
        if (!rotateAfterStaging) await emailStore.StageEmailUpdateAsync(validated, default);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => emailDb.SaveChangesAsync());

        await using var check = fixture.CreateDbContext();
        var persisted = (await new AccountStore(check).FindByIdAsync(account.Id, default))!;
        Assert.Null(persisted.Email);
        Assert.Equal(1, persisted.SessionGeneration);
        Assert.Equal("replacement-hash", persisted.PasswordHash);
        Assert.False(await check.EmailVerificationChallenges.AnyAsync(row => row.AccountId == account.Id.Value));
    }

    [Fact]
    public async Task Fresh_email_snapshot_with_an_older_tracked_row_never_writes_credentials_or_generation_back()
    {
        var account = await Seed();
        await using var emailDb = fixture.CreateDbContext();
        await emailDb.Accounts.SingleAsync(row => row.Id == account.Id.Value);
        await RotatePassword(account);
        var store = new AccountStore(emailDb);
        var current = (await store.FindByIdAsync(account.Id, default))!;
        Assert.Equal(1, current.SessionGeneration);
        current.AttachOrReplaceUnverifiedEmail(Email.Create($"fresh-{Guid.NewGuid():N}@example.com"));
        await store.StageEmailUpdateAsync(current, default);
        await emailDb.SaveChangesAsync();

        await using var check = fixture.CreateDbContext();
        var persisted = (await new AccountStore(check).FindByIdAsync(account.Id, default))!;
        Assert.Equal(current.Email, persisted.Email);
        Assert.Equal(1, persisted.SessionGeneration);
        Assert.Equal("replacement-hash", persisted.PasswordHash);
    }

    [Fact]
    public async Task Email_staging_preserves_a_credential_change_owned_by_the_same_unit_of_work()
    {
        var account = await Seed();
        await using var db = fixture.CreateDbContext();
        var store = new AccountStore(db);
        var changed = (await store.FindByIdAsync(account.Id, default))!;
        var expected = changed.SessionGeneration;
        changed.ChangePassword("replacement-hash");
        await store.StageCredentialUpdateAsync(changed, expected, default);
        changed.AttachOrReplaceUnverifiedEmail(Email.Create($"composed-{Guid.NewGuid():N}@example.com"));
        await store.StageEmailUpdateAsync(changed, default);
        await db.SaveChangesAsync();

        await using var check = fixture.CreateDbContext();
        var persisted = (await new AccountStore(check).FindByIdAsync(account.Id, default))!;
        Assert.Equal(changed.Email, persisted.Email);
        Assert.Equal(1, persisted.SessionGeneration);
        Assert.Equal("replacement-hash", persisted.PasswordHash);
    }

    private async Task<Account> Seed()
    {
        var account = Account.Register(Username.Create($"email{Guid.NewGuid():N}"[..25]), "original-hash", Now);
        await using var db = fixture.CreateDbContext();
        await new AccountStore(db).AddAsync(account, default);
        await db.SaveChangesAsync();
        return account;
    }

    private async Task RotatePassword(Account account)
    {
        await using var db = fixture.CreateDbContext();
        var store = new AccountStore(db);
        var changed = (await store.FindByIdAsync(account.Id, default))!;
        var expected = changed.SessionGeneration;
        changed.ChangePassword("replacement-hash");
        await store.StageCredentialUpdateAsync(changed, expected, default);
        await db.SaveChangesAsync();
    }
}
