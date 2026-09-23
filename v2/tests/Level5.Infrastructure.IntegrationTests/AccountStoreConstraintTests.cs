using Level5.Domain.Identity;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class AccountStoreConstraintTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Database_rejects_two_accounts_with_the_same_canonical_email()
    {
        var email = $"dup{Guid.NewGuid():N}@example.com";

        await using var db = fixture.CreateDbContext();
        var store = new AccountStore(db);
        await store.AddAsync(
            Account.Register(Username.Create($"a{Guid.NewGuid():N}"[..15]), "hash", Now, Email.Create(email)),
            CancellationToken.None);
        await db.SaveChangesAsync();

        await store.AddAsync(
            Account.Register(Username.Create($"b{Guid.NewGuid():N}"[..15]), "hash2", Now, Email.Create(email.ToUpperInvariant())),
            CancellationToken.None);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Database_allows_multiple_accounts_with_no_email()
    {
        await using var db = fixture.CreateDbContext();
        var store = new AccountStore(db);

        await store.AddAsync(Account.Register(Username.Create($"a{Guid.NewGuid():N}"[..15]), "hash", Now), CancellationToken.None);
        await store.AddAsync(Account.Register(Username.Create($"b{Guid.NewGuid():N}"[..15]), "hash2", Now), CancellationToken.None);

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Account_status_and_email_round_trip_through_persistence()
    {
        var email = Email.Create($"roundtrip{Guid.NewGuid():N}@example.com");
        var username = Username.Create($"r{Guid.NewGuid():N}"[..15]);

        await using var db = fixture.CreateDbContext();
        var store = new AccountStore(db);
        var account = Account.Register(username, "hash", Now, email);
        await store.AddAsync(account, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var readDb = fixture.CreateDbContext();
        var reloaded = await new AccountStore(readDb).FindByIdAsync(account.Id, CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.Equal(AccountStatus.Active, reloaded!.Status);
        Assert.Equal(email.Value, reloaded.Email!.Value);
        Assert.Equal(email.Canonical, reloaded.Email!.Canonical);
    }
}
