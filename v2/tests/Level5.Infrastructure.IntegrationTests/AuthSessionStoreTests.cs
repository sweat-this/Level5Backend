using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class AuthSessionStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private async Task<AccountId> SeedAccountAsync()
    {
        await using var db = fixture.CreateDbContext();
        var account = Account.Register(Username.Create($"a{Guid.NewGuid():N}"[..15]), "hash", Now);
        await new AccountStore(db).AddAsync(account, CancellationToken.None);
        await db.SaveChangesAsync();
        return account.Id;
    }

    [Fact]
    public async Task A_session_round_trips_through_persistence()
    {
        var accountId = await SeedAccountAsync();
        var session = AuthSession.Create(accountId, "hash-of-refresh-token", Now, Lifetime);

        await using var writeDb = fixture.CreateDbContext();
        await new AuthSessionStore(writeDb).AddAsync(session, CancellationToken.None);
        await writeDb.SaveChangesAsync();

        await using var readDb = fixture.CreateDbContext();
        var reloaded = await new AuthSessionStore(readDb).FindByIdAsync(session.Id, CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.Equal(accountId, reloaded!.AccountId);
        Assert.Equal("hash-of-refresh-token", reloaded.RefreshTokenHash);
        Assert.Null(reloaded.RevokedAt);
        Assert.Equal(0, reloaded.Revision);
    }

    [Fact]
    public async Task A_session_can_be_looked_up_by_its_refresh_token_hash()
    {
        var accountId = await SeedAccountAsync();
        var hash = $"hash-{Guid.NewGuid():N}";
        var session = AuthSession.Create(accountId, hash, Now, Lifetime);

        await using var writeDb = fixture.CreateDbContext();
        await new AuthSessionStore(writeDb).AddAsync(session, CancellationToken.None);
        await writeDb.SaveChangesAsync();

        await using var readDb = fixture.CreateDbContext();
        var found = await new AuthSessionStore(readDb).FindByRefreshTokenHashAsync(hash, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(session.Id, found!.Id);
    }

    [Fact]
    public async Task The_database_rejects_two_sessions_with_the_same_refresh_token_hash()
    {
        var accountId = await SeedAccountAsync();
        var sharedHash = $"dup-{Guid.NewGuid():N}";

        await using var db = fixture.CreateDbContext();
        var store = new AuthSessionStore(db);
        await store.AddAsync(AuthSession.Create(accountId, sharedHash, Now, Lifetime), CancellationToken.None);
        await db.SaveChangesAsync();

        await store.AddAsync(AuthSession.Create(accountId, sharedHash, Now, Lifetime), CancellationToken.None);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task TrySaveAsync_rotation_wins_on_the_correct_revision_and_loses_on_a_stale_one()
    {
        var accountId = await SeedAccountAsync();
        var original = AuthSession.Create(accountId, $"hash-{Guid.NewGuid():N}", Now, Lifetime);

        await using var setupDb = fixture.CreateDbContext();
        await new AuthSessionStore(setupDb).AddAsync(original, CancellationToken.None);
        await setupDb.SaveChangesAsync();

        // Two concurrent refresh requests both load the session at revision 0.
        await using var dbA = fixture.CreateDbContext();
        await using var dbB = fixture.CreateDbContext();
        var storeA = new AuthSessionStore(dbA);
        var storeB = new AuthSessionStore(dbB);

        var sessionA = await storeA.FindByIdAsync(original.Id, CancellationToken.None);
        var sessionB = await storeB.FindByIdAsync(original.Id, CancellationToken.None);

        sessionA!.Rotate($"hash-a-{Guid.NewGuid():N}", Now, Lifetime);
        var savedA = await storeA.TrySaveAsync(sessionA, expectedRevision: 0, CancellationToken.None);

        sessionB!.Rotate($"hash-b-{Guid.NewGuid():N}", Now, Lifetime);
        var savedB = await storeB.TrySaveAsync(sessionB, expectedRevision: 0, CancellationToken.None);

        Assert.True(savedA);
        Assert.False(savedB);

        await using var verifyDb = fixture.CreateDbContext();
        var final = await new AuthSessionStore(verifyDb).FindByIdAsync(original.Id, CancellationToken.None);
        Assert.Equal(sessionA.RefreshTokenHash, final!.RefreshTokenHash);
        Assert.Equal(1, final.Revision);
    }

    [Fact]
    public async Task TrySaveAsync_revocation_is_rejected_by_a_stale_revision_after_a_concurrent_rotation()
    {
        var accountId = await SeedAccountAsync();
        var original = AuthSession.Create(accountId, $"hash-{Guid.NewGuid():N}", Now, Lifetime);

        await using var setupDb = fixture.CreateDbContext();
        await new AuthSessionStore(setupDb).AddAsync(original, CancellationToken.None);
        await setupDb.SaveChangesAsync();

        await using var dbA = fixture.CreateDbContext();
        await using var dbB = fixture.CreateDbContext();
        var storeA = new AuthSessionStore(dbA);
        var storeB = new AuthSessionStore(dbB);

        var sessionA = await storeA.FindByIdAsync(original.Id, CancellationToken.None);
        var sessionB = await storeB.FindByIdAsync(original.Id, CancellationToken.None);

        // A refresh (rotation) wins the race...
        sessionA!.Rotate($"hash-a-{Guid.NewGuid():N}", Now, Lifetime);
        Assert.True(await storeA.TrySaveAsync(sessionA, expectedRevision: 0, CancellationToken.None));

        // ...so a concurrent logout (revocation) loaded from the same stale revision must not win.
        sessionB!.Revoke(Now);
        var savedB = await storeB.TrySaveAsync(sessionB, expectedRevision: 0, CancellationToken.None);

        Assert.False(savedB);

        await using var verifyDb = fixture.CreateDbContext();
        var final = await new AuthSessionStore(verifyDb).FindByIdAsync(original.Id, CancellationToken.None);
        Assert.Null(final!.RevokedAt);
    }

    [Fact]
    public async Task The_raw_refresh_secret_is_never_what_gets_persisted()
    {
        var accountId = await SeedAccountAsync();
        const string rawSecret = "this-is-the-raw-client-secret-never-store-me";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawSecret)));
        var session = AuthSession.Create(accountId, hash, Now, Lifetime);

        await using var db = fixture.CreateDbContext();
        await new AuthSessionStore(db).AddAsync(session, CancellationToken.None);
        await db.SaveChangesAsync();

        var storedValues = await db.Database.SqlQuery<string>(
                $"SELECT \"RefreshTokenHash\" FROM auth_sessions WHERE \"Id\" = {session.Id.Value}")
            .ToListAsync();

        var storedValue = Assert.Single(storedValues);
        Assert.NotEqual(rawSecret, storedValue);
        Assert.Equal(hash, storedValue);
    }
}
