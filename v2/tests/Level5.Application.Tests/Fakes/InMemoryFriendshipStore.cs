using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Domain.Social;

namespace Level5.Application.Tests.Fakes;

public sealed class InMemoryFriendshipStore : IFriendshipStore
{
    private readonly Dictionary<Guid, FriendRequest> _requests = [];
    private readonly Dictionary<Guid, Friendship> _friendships = [];

    public Task<FriendRequest?> FindRequestByIdAsync(FriendRequestId id, CancellationToken cancellationToken)
        => Task.FromResult(_requests.GetValueOrDefault(id.Value));

    public Task<FriendRequest?> FindPendingRequestBetweenAsync(PlayerId a, PlayerId b, CancellationToken cancellationToken)
        => Task.FromResult(_requests.Values.SingleOrDefault(r =>
            r.Status == FriendRequestStatus.Pending &&
            ((r.FromPlayerId == a && r.ToPlayerId == b) || (r.FromPlayerId == b && r.ToPlayerId == a))));

    public Task<IReadOnlyList<FriendRequest>> ListIncomingAsync(PlayerId playerId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<FriendRequest>>(
            [.. _requests.Values.Where(r => r.ToPlayerId == playerId && r.Status == FriendRequestStatus.Pending)]);

    public Task<IReadOnlyList<FriendRequest>> ListOutgoingAsync(PlayerId playerId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<FriendRequest>>(
            [.. _requests.Values.Where(r => r.FromPlayerId == playerId && r.Status == FriendRequestStatus.Pending)]);

    public Task AddRequestAsync(FriendRequest request, CancellationToken cancellationToken)
    {
        _requests[request.Id.Value] = request;
        return Task.CompletedTask;
    }

    public Task UpdateRequestAsync(FriendRequest request, CancellationToken cancellationToken)
    {
        _requests[request.Id.Value] = request;
        return Task.CompletedTask;
    }

    public Task<bool> AreFriendsAsync(PlayerId a, PlayerId b, CancellationToken cancellationToken)
    {
        var (lower, upper) = Friendship.Order(a, b);
        return Task.FromResult(_friendships.Values.Any(f => f.LowerPlayerId == lower && f.UpperPlayerId == upper));
    }

    public Task<Friendship?> FindFriendshipBetweenAsync(PlayerId a, PlayerId b, CancellationToken cancellationToken)
    {
        var (lower, upper) = Friendship.Order(a, b);
        return Task.FromResult(_friendships.Values.SingleOrDefault(f => f.LowerPlayerId == lower && f.UpperPlayerId == upper));
    }

    public Task<IReadOnlyList<Friendship>> ListFriendsAsync(PlayerId playerId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Friendship>>([.. _friendships.Values.Where(f => f.Involves(playerId))]);

    public Task AddFriendshipAsync(Friendship friendship, CancellationToken cancellationToken)
    {
        _friendships[friendship.Id.Value] = friendship;
        return Task.CompletedTask;
    }

    public Task RemoveFriendshipAsync(Friendship friendship, CancellationToken cancellationToken)
    {
        _friendships.Remove(friendship.Id.Value);
        return Task.CompletedTask;
    }
}
