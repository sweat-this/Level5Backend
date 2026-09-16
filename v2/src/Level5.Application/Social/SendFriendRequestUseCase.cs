using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Ids;
using Level5.Domain.Social;

namespace Level5.Application.Social;

public sealed record SendFriendRequestRequest(PlayerId FromPlayerId, PlayerId ToPlayerId);

public sealed record FriendRequestResult(FriendRequestId Id, PlayerId FromPlayerId, PlayerId ToPlayerId, FriendRequestStatus Status);

public sealed class SendFriendRequestUseCase(
    IFriendshipStore friendshipStore,
    IPlayerProfileStore playerProfileStore,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<FriendRequestResult> ExecuteAsync(SendFriendRequestRequest request, CancellationToken cancellationToken)
    {
        if (await playerProfileStore.FindByIdAsync(request.ToPlayerId, cancellationToken) is null)
        {
            throw new NotFoundException("No player with that id was found.");
        }

        if (await friendshipStore.AreFriendsAsync(request.FromPlayerId, request.ToPlayerId, cancellationToken))
        {
            throw new ConflictException("You are already friends with this player.");
        }

        if (await friendshipStore.FindPendingRequestBetweenAsync(request.FromPlayerId, request.ToPlayerId, cancellationToken) is not null)
        {
            throw new ConflictException("A friend request is already pending between these players.");
        }

        var friendRequest = FriendRequest.Create(request.FromPlayerId, request.ToPlayerId, clock.UtcNow);
        await friendshipStore.AddRequestAsync(friendRequest, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new FriendRequestResult(friendRequest.Id, friendRequest.FromPlayerId, friendRequest.ToPlayerId, friendRequest.Status);
    }
}
