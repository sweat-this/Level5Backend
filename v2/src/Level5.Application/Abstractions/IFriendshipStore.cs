using Level5.Domain.Ids;
using Level5.Domain.Social;

namespace Level5.Application.Abstractions;

public interface IFriendshipStore
{
    Task<FriendRequest?> FindRequestByIdAsync(FriendRequestId id, CancellationToken cancellationToken);

    /// <summary>Used to reject a duplicate send in either direction while one is already pending.</summary>
    Task<FriendRequest?> FindPendingRequestBetweenAsync(PlayerId a, PlayerId b, CancellationToken cancellationToken);

    Task<IReadOnlyList<FriendRequest>> ListIncomingAsync(PlayerId playerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FriendRequest>> ListOutgoingAsync(PlayerId playerId, CancellationToken cancellationToken);

    Task AddRequestAsync(FriendRequest request, CancellationToken cancellationToken);

    /// <summary>Writes back a status change (Accept/Decline/Cancel) made to a previously-loaded request.</summary>
    Task UpdateRequestAsync(FriendRequest request, CancellationToken cancellationToken);

    Task<bool> AreFriendsAsync(PlayerId a, PlayerId b, CancellationToken cancellationToken);

    Task<Friendship?> FindFriendshipBetweenAsync(PlayerId a, PlayerId b, CancellationToken cancellationToken);

    Task<IReadOnlyList<Friendship>> ListFriendsAsync(PlayerId playerId, CancellationToken cancellationToken);

    Task AddFriendshipAsync(Friendship friendship, CancellationToken cancellationToken);

    Task RemoveFriendshipAsync(Friendship friendship, CancellationToken cancellationToken);
}
