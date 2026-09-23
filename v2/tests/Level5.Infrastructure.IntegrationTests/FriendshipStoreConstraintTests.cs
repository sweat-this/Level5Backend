using Level5.Domain.Social;
using Level5.Infrastructure.Persistence;
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
        await using var db = fixture.CreateDbContext();
        var a = await PlayerSeeding.CreatePlayerAsync(db, "A", Now);
        var b = await PlayerSeeding.CreatePlayerAsync(db, "B", Now);

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
        await using var db = fixture.CreateDbContext();
        var a = await PlayerSeeding.CreatePlayerAsync(db, "A", Now);
        var b = await PlayerSeeding.CreatePlayerAsync(db, "B", Now);

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
        await using var db = fixture.CreateDbContext();
        var a = await PlayerSeeding.CreatePlayerAsync(db, "A", Now);
        var b = await PlayerSeeding.CreatePlayerAsync(db, "B", Now);

        var store = new FriendshipStore(db);
        await store.AddFriendshipAsync(Friendship.Between(a, b, Now), CancellationToken.None);
        await db.SaveChangesAsync();

        await store.AddFriendshipAsync(Friendship.Between(b, a, Now), CancellationToken.None);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Database_rejects_a_crossed_direction_pending_request_committed_after_the_first()
    {
        await using var db = fixture.CreateDbContext();
        var a = await PlayerSeeding.CreatePlayerAsync(db, "A", Now);
        var b = await PlayerSeeding.CreatePlayerAsync(db, "B", Now);

        var store = new FriendshipStore(db);
        await store.AddRequestAsync(FriendRequest.Create(a, b, Now), CancellationToken.None);
        await db.SaveChangesAsync();

        // B -> A is a distinct (FromPlayerId, ToPlayerId) pair from A -> B, so the same-direction
        // index alone would not catch this - only the canonical (Lower, Upper) index does.
        await store.AddRequestAsync(FriendRequest.Create(b, a, Now), CancellationToken.None);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Two_genuinely_concurrent_crossed_sends_leave_exactly_one_pending_request()
    {
        await using var seedDb = fixture.CreateDbContext();
        var a = await PlayerSeeding.CreatePlayerAsync(seedDb, "A", Now);
        var b = await PlayerSeeding.CreatePlayerAsync(seedDb, "B", Now);

        // Two separate connections/contexts both insert without seeing each other's uncommitted
        // row, so this exercises the database constraint (not the application pre-check, which
        // only queries whichever rows are already committed).
        await using var dbA = fixture.CreateDbContext();
        await using var dbB = fixture.CreateDbContext();
        var storeA = new FriendshipStore(dbA);
        var storeB = new FriendshipStore(dbB);

        await storeA.AddRequestAsync(FriendRequest.Create(a, b, Now), CancellationToken.None);
        await storeB.AddRequestAsync(FriendRequest.Create(b, a, Now), CancellationToken.None);

        var savedA = await TrySaveAsync(dbA);
        var savedB = await TrySaveAsync(dbB);

        Assert.True(savedA ^ savedB, "Exactly one of the two crossed sends should have committed.");

        await using var verifyDb = fixture.CreateDbContext();
        var pendingCount = await verifyDb.FriendRequests.CountAsync(r =>
            r.Status == nameof(FriendRequestStatus.Pending) &&
            ((r.FromPlayerId == a.Value && r.ToPlayerId == b.Value) || (r.FromPlayerId == b.Value && r.ToPlayerId == a.Value)));
        Assert.Equal(1, pendingCount);
    }

    private static async Task<bool> TrySaveAsync(Level5V2DbContext db)
    {
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }
}
