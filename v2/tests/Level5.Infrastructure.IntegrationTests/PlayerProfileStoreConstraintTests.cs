using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Domain.Players;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class PlayerProfileStoreConstraintTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Database_rejects_two_profiles_with_the_same_tag()
    {
        var tag = PlayerTag.Create($"Dup{Guid.NewGuid():N}"[..10] + "#1234");

        await using var db = fixture.CreateDbContext();
        var store = new PlayerProfileStore(db);
        await store.AddAsync(PlayerProfile.Create(AccountId.New(), "First", tag, Now), CancellationToken.None);
        await db.SaveChangesAsync();

        await store.AddAsync(PlayerProfile.Create(AccountId.New(), "Second", tag, Now), CancellationToken.None);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Database_rejects_two_profiles_for_the_same_account()
    {
        var accountId = AccountId.New();

        await using var db = fixture.CreateDbContext();
        var store = new PlayerProfileStore(db);
        await store.AddAsync(PlayerProfile.Create(accountId, "First", PlayerTag.Create("First#0001"), Now), CancellationToken.None);
        await db.SaveChangesAsync();

        await store.AddAsync(PlayerProfile.Create(accountId, "Second", PlayerTag.Create("Second#0002"), Now), CancellationToken.None);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Database_rejects_two_accounts_with_the_same_username_case_insensitively()
    {
        await using var db = fixture.CreateDbContext();
        var store = new AccountStore(db);
        await store.AddAsync(Account.Register(Username.Create("CaseTest"), "hash", Now), CancellationToken.None);
        await db.SaveChangesAsync();

        await store.AddAsync(Account.Register(Username.Create("casetest"), "hash2", Now), CancellationToken.None);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task FindByIdsAsync_batches_a_lookup_across_multiple_ids_in_one_query()
    {
        await using var db = fixture.CreateDbContext();
        var store = new PlayerProfileStore(db);
        var a = PlayerProfile.Create(AccountId.New(), "BatchA", PlayerTag.Create("BatchA#0001"), Now);
        var b = PlayerProfile.Create(AccountId.New(), "BatchB", PlayerTag.Create("BatchB#0002"), Now);
        var unrelated = PlayerProfile.Create(AccountId.New(), "BatchC", PlayerTag.Create("BatchC#0003"), Now);
        await store.AddAsync(a, CancellationToken.None);
        await store.AddAsync(b, CancellationToken.None);
        await store.AddAsync(unrelated, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var readDb = fixture.CreateDbContext();
        var results = await new PlayerProfileStore(readDb).FindByIdsAsync([a.Id, b.Id], CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, p => p.Id == a.Id);
        Assert.Contains(results, p => p.Id == b.Id);
        Assert.DoesNotContain(results, p => p.Id == unrelated.Id);
    }

    [Fact]
    public async Task FindByIdsAsync_with_no_ids_returns_an_empty_list_without_querying()
    {
        await using var db = fixture.CreateDbContext();
        var results = await new PlayerProfileStore(db).FindByIdsAsync([], CancellationToken.None);

        Assert.Empty(results);
    }
}
