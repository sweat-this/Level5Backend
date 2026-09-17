using Level5.Application.Common;
using Level5.Domain.Ids;
using Level5.Domain.Social;
using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

/// <summary>
/// Exercises FriendRequestRow.Revision as an optimistic-concurrency token against real Postgres.
/// Each test loads the same Pending request through two separate DbContexts (simulating two
/// concurrent requests that both observed the same initial state) before either commits, then
/// commits one and asserts the other is rejected rather than silently overwriting it - mirroring
/// VersusSeriesStoreTests' TrySaveAsync race test for the competition aggregate.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FriendRequestConcurrencyTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private async Task<(FriendRequestId RequestId, PlayerId Sender, PlayerId Recipient)> SeedPendingRequestAsync()
    {
        var sender = PlayerId.New();
        var recipient = PlayerId.New();

        await using var db = fixture.CreateDbContext();
        var store = new FriendshipStore(db);
        var request = FriendRequest.Create(sender, recipient, Now);
        await store.AddRequestAsync(request, CancellationToken.None);
        await db.SaveChangesAsync();

        return (request.Id, sender, recipient);
    }

    [Fact]
    public async Task A_stale_transition_loaded_before_a_winning_transition_committed_is_rejected()
    {
        var (requestId, sender, recipient) = await SeedPendingRequestAsync();

        await using var dbA = fixture.CreateDbContext();
        await using var dbB = fixture.CreateDbContext();
        var storeA = new FriendshipStore(dbA);
        var storeB = new FriendshipStore(dbB);

        // Both load the same Pending request at the same revision before either writes.
        var requestA = await storeA.FindRequestByIdAsync(requestId, CancellationToken.None);
        var requestB = await storeB.FindRequestByIdAsync(requestId, CancellationToken.None);

        requestA!.Cancel(sender, Now);
        await storeA.UpdateRequestAsync(requestA, CancellationToken.None);
        await new EfUnitOfWork(dbA).SaveChangesAsync(CancellationToken.None);

        // B's in-memory copy still thinks the request is Pending (it was loaded before A committed).
        requestB!.Decline(recipient, Now);
        await storeB.UpdateRequestAsync(requestB, CancellationToken.None);
        await Assert.ThrowsAsync<ConflictException>(() => new EfUnitOfWork(dbB).SaveChangesAsync(CancellationToken.None));

        await using var verifyDb = fixture.CreateDbContext();
        var final = await new FriendshipStore(verifyDb).FindRequestByIdAsync(requestId, CancellationToken.None);
        Assert.Equal(FriendRequestStatus.Cancelled, final!.Status);
        Assert.Equal(1, final.Revision);
    }

    [Fact]
    public async Task Accept_vs_Decline_exactly_one_commits_and_no_mixed_state_results()
    {
        var (requestId, sender, recipient) = await SeedPendingRequestAsync();

        await using var dbA = fixture.CreateDbContext();
        await using var dbB = fixture.CreateDbContext();
        var storeA = new FriendshipStore(dbA);
        var storeB = new FriendshipStore(dbB);

        var requestA = await storeA.FindRequestByIdAsync(requestId, CancellationToken.None);
        var requestB = await storeB.FindRequestByIdAsync(requestId, CancellationToken.None);

        var friendship = requestA!.Accept(recipient, Now);
        await storeA.UpdateRequestAsync(requestA, CancellationToken.None);
        await storeA.AddFriendshipAsync(friendship, CancellationToken.None);
        await new EfUnitOfWork(dbA).SaveChangesAsync(CancellationToken.None);

        requestB!.Decline(recipient, Now);
        await storeB.UpdateRequestAsync(requestB, CancellationToken.None);
        await Assert.ThrowsAsync<ConflictException>(() => new EfUnitOfWork(dbB).SaveChangesAsync(CancellationToken.None));

        await using var verifyDb = fixture.CreateDbContext();
        var verifyStore = new FriendshipStore(verifyDb);
        var final = await verifyStore.FindRequestByIdAsync(requestId, CancellationToken.None);
        Assert.Equal(FriendRequestStatus.Accepted, final!.Status);
        Assert.True(await verifyStore.AreFriendsAsync(sender, recipient, CancellationToken.None));
    }

    [Fact]
    public async Task Accept_vs_Cancel_exactly_one_commits_and_no_mixed_state_results()
    {
        var (requestId, sender, recipient) = await SeedPendingRequestAsync();

        await using var dbA = fixture.CreateDbContext();
        await using var dbB = fixture.CreateDbContext();
        var storeA = new FriendshipStore(dbA);
        var storeB = new FriendshipStore(dbB);

        var requestA = await storeA.FindRequestByIdAsync(requestId, CancellationToken.None);
        var requestB = await storeB.FindRequestByIdAsync(requestId, CancellationToken.None);

        var friendship = requestA!.Accept(recipient, Now);
        await storeA.UpdateRequestAsync(requestA, CancellationToken.None);
        await storeA.AddFriendshipAsync(friendship, CancellationToken.None);
        await new EfUnitOfWork(dbA).SaveChangesAsync(CancellationToken.None);

        requestB!.Cancel(sender, Now);
        await storeB.UpdateRequestAsync(requestB, CancellationToken.None);
        await Assert.ThrowsAsync<ConflictException>(() => new EfUnitOfWork(dbB).SaveChangesAsync(CancellationToken.None));

        await using var verifyDb = fixture.CreateDbContext();
        var verifyStore = new FriendshipStore(verifyDb);
        var final = await verifyStore.FindRequestByIdAsync(requestId, CancellationToken.None);
        Assert.Equal(FriendRequestStatus.Accepted, final!.Status);
        Assert.True(await verifyStore.AreFriendsAsync(sender, recipient, CancellationToken.None));
    }

    [Fact]
    public async Task Accept_vs_Accept_at_most_one_commits_and_exactly_one_friendship_exists()
    {
        var (requestId, sender, recipient) = await SeedPendingRequestAsync();

        await using var dbA = fixture.CreateDbContext();
        await using var dbB = fixture.CreateDbContext();
        var storeA = new FriendshipStore(dbA);
        var storeB = new FriendshipStore(dbB);

        var requestA = await storeA.FindRequestByIdAsync(requestId, CancellationToken.None);
        var requestB = await storeB.FindRequestByIdAsync(requestId, CancellationToken.None);

        var friendshipA = requestA!.Accept(recipient, Now);
        await storeA.UpdateRequestAsync(requestA, CancellationToken.None);
        await storeA.AddFriendshipAsync(friendshipA, CancellationToken.None);
        await new EfUnitOfWork(dbA).SaveChangesAsync(CancellationToken.None);

        // B's Accept, loaded from the same stale Pending snapshot, is the losing duplicate accept.
        var friendshipB = requestB!.Accept(recipient, Now);
        await storeB.UpdateRequestAsync(requestB, CancellationToken.None);
        await storeB.AddFriendshipAsync(friendshipB, CancellationToken.None);
        await Assert.ThrowsAsync<ConflictException>(() => new EfUnitOfWork(dbB).SaveChangesAsync(CancellationToken.None));

        await using var verifyDb = fixture.CreateDbContext();
        var friendshipCount = await verifyDb.Friendships.CountAsync(f =>
            (f.LowerPlayerId == sender.Value || f.LowerPlayerId == recipient.Value) &&
            (f.UpperPlayerId == sender.Value || f.UpperPlayerId == recipient.Value));
        Assert.Equal(1, friendshipCount);

        var final = await new FriendshipStore(verifyDb).FindRequestByIdAsync(requestId, CancellationToken.None);
        Assert.Equal(FriendRequestStatus.Accepted, final!.Status);
    }
}
