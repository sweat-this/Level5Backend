using Level5.Domain.Ids;
using Level5.Domain.Social;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class FriendshipStoreConstraintTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Database_rejects_a_second_pending_request_between_the_same_pair()
    {
        var a = PlayerId.New();
        var b = PlayerId.New();

        await using var db = fixture.CreateDbContext();
        var store = new FriendshipStore(db);
        await store.AddRequestAsync(FriendRequest.Create(a, b, Now), CancellationToken.None);
        await db.SaveChangesAsync();

        // The application layer already checks for an existing pending request before inserting,
        // but the real safety net against a race between two concurrent sends is this unique
        // partial index - verify it actually rejects a duplicate at the database level.
        await store.AddRequestAsync(FriendRequest.Create(a, b, Now), CancellationToken.None);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Database_allows_a_new_pending_request_after_the_first_was_declined()
    {
        var a = PlayerId.New();
        var b = PlayerId.New();

        await using var db = fixture.CreateDbContext();
        var store = new FriendshipStore(db);
        var first = FriendRequest.Create(a, b, Now);
        await store.AddRequestAsync(first, CancellationToken.None);
        await db.SaveChangesAsync();

        first.Decline(b, Now);
        await store.UpdateRequestAsync(first, CancellationToken.None);
        await db.SaveChangesAsync();

        await store.AddRequestAsync(FriendRequest.Create(a, b, Now), CancellationToken.None);
        await db.SaveChangesAsync(); // should not throw
    }

    [Fact]
    public async Task Database_rejects_a_duplicate_friendship_regardless_of_argument_order()
    {
        var a = PlayerId.New();
        var b = PlayerId.New();

        await using var db = fixture.CreateDbContext();
        var store = new FriendshipStore(db);
        await store.AddFriendshipAsync(Friendship.Between(a, b, Now), CancellationToken.None);
        await db.SaveChangesAsync();

        await store.AddFriendshipAsync(Friendship.Between(b, a, Now), CancellationToken.None);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
