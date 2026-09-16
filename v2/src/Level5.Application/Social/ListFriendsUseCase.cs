using Level5.Application.Abstractions;
using Level5.Domain.Ids;

namespace Level5.Application.Social;

public sealed record FriendSummary(PlayerId PlayerId, string DisplayName, string Tag, DateTimeOffset FriendsSince);

public sealed class ListFriendsUseCase(IFriendshipStore friendshipStore, IPlayerProfileStore playerProfileStore)
{
    public async Task<IReadOnlyList<FriendSummary>> ExecuteAsync(PlayerId actingPlayerId, CancellationToken cancellationToken)
    {
        var friendships = await friendshipStore.ListFriendsAsync(actingPlayerId, cancellationToken);
        if (friendships.Count == 0)
        {
            return [];
        }

        var otherPlayerIds = friendships.Select(f => f.OtherPlayer(actingPlayerId)).ToArray();
        var profiles = await playerProfileStore.FindByIdsAsync(otherPlayerIds, cancellationToken);
        var profilesById = profiles.ToDictionary(p => p.Id);

        var summaries = new List<FriendSummary>(friendships.Count);
        foreach (var friendship in friendships)
        {
            if (!profilesById.TryGetValue(friendship.OtherPlayer(actingPlayerId), out var profile))
            {
                continue;
            }

            summaries.Add(new FriendSummary(profile.Id, profile.DisplayName, profile.Tag.Value, friendship.CreatedAt));
        }

        return summaries;
    }
}
