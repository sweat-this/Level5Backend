using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Domain.Social;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.Persistence.Repositories;

public sealed class FriendshipStore(Level5V2DbContext db) : IFriendshipStore
{
    // Deliberately tracked (no AsNoTracking), unlike VersusSeriesStore.FindByIdAsync /
    // AuthSessionStore.FindByIdAsync: those stores enforce their Revision concurrency token
    // explicitly via ExecuteUpdateAsync(...).Where(r => r.Revision == expectedRevision), so their
    // reads can safely be untracked. This store instead relies on FriendRequestRow.Revision being
    // configured as an EF concurrency token (Level5V2DbContext) plus EF's identity map: because
    // this query stays tracked, UpdateRequestAsync's re-query below returns the SAME tracked
    // instance rather than refreshing it, so SaveChanges compares against the Revision that was
    // current when THIS call ran, not whatever is in the database by the time of the write. Adding
    // AsNoTracking here would make that comparison silently vacuous (current DB value vs. itself),
    // defeating Accept/Decline/Cancel's race protection with no exception - see IFriendshipStore.
    public async Task<FriendRequest?> FindRequestByIdAsync(FriendRequestId id, CancellationToken cancellationToken)
    {
        var row = await db.FriendRequests.SingleOrDefaultAsync(r => r.Id == id.Value, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task<FriendRequest?> FindPendingRequestBetweenAsync(PlayerId a, PlayerId b, CancellationToken cancellationToken)
    {
        var row = await db.FriendRequests.SingleOrDefaultAsync(r =>
            r.Status == nameof(FriendRequestStatus.Pending) &&
            ((r.FromPlayerId == a.Value && r.ToPlayerId == b.Value) || (r.FromPlayerId == b.Value && r.ToPlayerId == a.Value)),
            cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<FriendRequest>> ListIncomingAsync(PlayerId playerId, CancellationToken cancellationToken)
    {
        var rows = await db.FriendRequests
            .Where(r => r.ToPlayerId == playerId.Value && r.Status == nameof(FriendRequestStatus.Pending))
            .ToListAsync(cancellationToken);
        return [.. rows.Select(ToDomain)];
    }

    public async Task<IReadOnlyList<FriendRequest>> ListOutgoingAsync(PlayerId playerId, CancellationToken cancellationToken)
    {
        var rows = await db.FriendRequests
            .Where(r => r.FromPlayerId == playerId.Value && r.Status == nameof(FriendRequestStatus.Pending))
            .ToListAsync(cancellationToken);
        return [.. rows.Select(ToDomain)];
    }

    public async Task AddRequestAsync(FriendRequest request, CancellationToken cancellationToken)
    {
        var (lower, upper) = Friendship.Order(request.FromPlayerId, request.ToPlayerId);
        await db.FriendRequests.AddAsync(new FriendRequestRow
        {
            Id = request.Id.Value,
            FromPlayerId = request.FromPlayerId.Value,
            ToPlayerId = request.ToPlayerId.Value,
            LowerPlayerId = lower.Value,
            UpperPlayerId = upper.Value,
            Status = request.Status.ToString(),
            CreatedAt = request.CreatedAt,
            RespondedAt = request.RespondedAt,
            Revision = request.Revision
        }, cancellationToken);
    }

    public async Task UpdateRequestAsync(FriendRequest request, CancellationToken cancellationToken)
    {
        var row = await db.FriendRequests.SingleAsync(r => r.Id == request.Id.Value, cancellationToken);
        row.Status = request.Status.ToString();
        row.RespondedAt = request.RespondedAt;
        row.Revision = request.Revision;
    }

    public Task<bool> AreFriendsAsync(PlayerId a, PlayerId b, CancellationToken cancellationToken)
    {
        var (lower, upper) = Friendship.Order(a, b);
        return db.Friendships.AnyAsync(f => f.LowerPlayerId == lower.Value && f.UpperPlayerId == upper.Value, cancellationToken);
    }

    public async Task<Friendship?> FindFriendshipBetweenAsync(PlayerId a, PlayerId b, CancellationToken cancellationToken)
    {
        var (lower, upper) = Friendship.Order(a, b);
        var row = await db.Friendships.SingleOrDefaultAsync(f => f.LowerPlayerId == lower.Value && f.UpperPlayerId == upper.Value, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<Friendship>> ListFriendsAsync(PlayerId playerId, CancellationToken cancellationToken)
    {
        var rows = await db.Friendships
            .Where(f => f.LowerPlayerId == playerId.Value || f.UpperPlayerId == playerId.Value)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(ToDomain)];
    }

    public async Task AddFriendshipAsync(Friendship friendship, CancellationToken cancellationToken)
    {
        await db.Friendships.AddAsync(new FriendshipRow
        {
            Id = friendship.Id.Value,
            LowerPlayerId = friendship.LowerPlayerId.Value,
            UpperPlayerId = friendship.UpperPlayerId.Value,
            CreatedAt = friendship.CreatedAt
        }, cancellationToken);
    }

    public async Task RemoveFriendshipAsync(Friendship friendship, CancellationToken cancellationToken)
    {
        var row = await db.Friendships.SingleAsync(f => f.Id == friendship.Id.Value, cancellationToken);
        db.Friendships.Remove(row);
    }

    private static FriendRequest ToDomain(FriendRequestRow row) => FriendRequest.Rehydrate(
        new FriendRequestId(row.Id), new PlayerId(row.FromPlayerId), new PlayerId(row.ToPlayerId),
        Enum.Parse<FriendRequestStatus>(row.Status), row.CreatedAt, row.RespondedAt, row.Revision);

    private static Friendship ToDomain(FriendshipRow row) => Friendship.Rehydrate(
        new FriendshipId(row.Id), new PlayerId(row.LowerPlayerId), new PlayerId(row.UpperPlayerId), row.CreatedAt);
}
