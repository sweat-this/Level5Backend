using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Ids;

namespace Level5.Application.Social;

public sealed record RemoveFriendRequest(PlayerId ActingPlayerId, PlayerId OtherPlayerId);

public sealed class RemoveFriendUseCase(IFriendshipStore friendshipStore, IUnitOfWork unitOfWork)
{
    public async Task ExecuteAsync(RemoveFriendRequest request, CancellationToken cancellationToken)
    {
        var friendship = await friendshipStore.FindFriendshipBetweenAsync(request.ActingPlayerId, request.OtherPlayerId, cancellationToken)
            ?? throw new NotFoundException("You are not friends with this player.");

        await friendshipStore.RemoveFriendshipAsync(friendship, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
