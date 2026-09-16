using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Domain.Social;

namespace Level5.Application.Social;

public sealed record FriendRequestSummary(FriendRequestId Id, PlayerId FromPlayerId, PlayerId ToPlayerId, FriendRequestStatus Status, DateTimeOffset CreatedAt);

public sealed class ListIncomingFriendRequestsUseCase(IFriendshipStore friendshipStore)
{
    public async Task<IReadOnlyList<FriendRequestSummary>> ExecuteAsync(PlayerId actingPlayerId, CancellationToken cancellationToken)
    {
        var requests = await friendshipStore.ListIncomingAsync(actingPlayerId, cancellationToken);
        return [.. requests.Select(r => new FriendRequestSummary(r.Id, r.FromPlayerId, r.ToPlayerId, r.Status, r.CreatedAt))];
    }
}

public sealed class ListOutgoingFriendRequestsUseCase(IFriendshipStore friendshipStore)
{
    public async Task<IReadOnlyList<FriendRequestSummary>> ExecuteAsync(PlayerId actingPlayerId, CancellationToken cancellationToken)
    {
        var requests = await friendshipStore.ListOutgoingAsync(actingPlayerId, cancellationToken);
        return [.. requests.Select(r => new FriendRequestSummary(r.Id, r.FromPlayerId, r.ToPlayerId, r.Status, r.CreatedAt))];
    }
}
