using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Ids;

namespace Level5.Application.Social;

public sealed record AcceptFriendRequestRequest(PlayerId ActingPlayerId, FriendRequestId RequestId);

public sealed class AcceptFriendRequestUseCase(
    IFriendshipStore friendshipStore,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task ExecuteAsync(AcceptFriendRequestRequest request, CancellationToken cancellationToken)
    {
        var friendRequest = await friendshipStore.FindRequestByIdAsync(request.RequestId, cancellationToken)
            ?? throw new NotFoundException("Friend request not found.");

        var friendship = friendRequest.Accept(request.ActingPlayerId, clock.UtcNow);

        await friendshipStore.UpdateRequestAsync(friendRequest, cancellationToken);
        await friendshipStore.AddFriendshipAsync(friendship, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
