using Level5.Application.Abstractions;
using Level5.Application.Players;
using Level5.Domain.Ids;
using Level5.Domain.Social;

namespace Level5.Application.Social;

public sealed record FriendRequestListItem(
    FriendRequestId Id, PlayerId FromPlayerId, PlayerId ToPlayerId, FriendRequestStatus Status, DateTimeOffset CreatedAt,
    PublicPlayerSummary OtherPlayer);

public sealed class ListIncomingFriendRequestsUseCase(IFriendshipStore friendshipStore, IPlayerProfileStore playerProfileStore)
{
    public async Task<IReadOnlyList<FriendRequestListItem>> ExecuteAsync(PlayerId actingPlayerId, CancellationToken cancellationToken)
    {
        var requests = await friendshipStore.ListIncomingAsync(actingPlayerId, cancellationToken);
        if (requests.Count == 0)
        {
            return [];
        }

        var senders = await PublicPlayerSummaries.ResolveAsync(playerProfileStore, [.. requests.Select(r => r.FromPlayerId)], cancellationToken);

        var items = new List<FriendRequestListItem>(requests.Count);
        foreach (var r in requests)
        {
            // A sender profile that unexpectedly fails to resolve drops just this row rather than
            // failing the whole list - matches ListFriendsUseCase's established orphan-safe pattern.
            if (!senders.TryGetValue(r.FromPlayerId, out var sender))
            {
                continue;
            }

            items.Add(new FriendRequestListItem(r.Id, r.FromPlayerId, r.ToPlayerId, r.Status, r.CreatedAt, sender));
        }

        return items;
    }
}

public sealed class ListOutgoingFriendRequestsUseCase(IFriendshipStore friendshipStore, IPlayerProfileStore playerProfileStore)
{
    public async Task<IReadOnlyList<FriendRequestListItem>> ExecuteAsync(PlayerId actingPlayerId, CancellationToken cancellationToken)
    {
        var requests = await friendshipStore.ListOutgoingAsync(actingPlayerId, cancellationToken);
        if (requests.Count == 0)
        {
            return [];
        }

        var recipients = await PublicPlayerSummaries.ResolveAsync(playerProfileStore, [.. requests.Select(r => r.ToPlayerId)], cancellationToken);

        var items = new List<FriendRequestListItem>(requests.Count);
        foreach (var r in requests)
        {
            if (!recipients.TryGetValue(r.ToPlayerId, out var recipient))
            {
                continue;
            }

            items.Add(new FriendRequestListItem(r.Id, r.FromPlayerId, r.ToPlayerId, r.Status, r.CreatedAt, recipient));
        }

        return items;
    }
}
