using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Ids;

namespace Level5.Application.Social;

public sealed record CancelFriendRequestRequest(PlayerId ActingPlayerId, FriendRequestId RequestId);

public sealed class CancelFriendRequestUseCase(IFriendshipStore friendshipStore, IUnitOfWork unitOfWork, IClock clock)
{
    public async Task ExecuteAsync(CancelFriendRequestRequest request, CancellationToken cancellationToken)
    {
        var friendRequest = await friendshipStore.FindRequestByIdAsync(request.RequestId, cancellationToken)
            ?? throw new NotFoundException("Friend request not found.");

        friendRequest.Cancel(request.ActingPlayerId, clock.UtcNow);
        await friendshipStore.UpdateRequestAsync(friendRequest, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
