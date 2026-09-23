using Level5.Application.Abstractions;
using Level5.Domain.Ids;

namespace Level5.Application.Players;

/// <summary>The only player identity fields safe to expose across a read contract - never AccountId.</summary>
public sealed record PublicPlayerSummary(PlayerId PlayerId, string DisplayName, string Tag);

/// <summary>
/// Batches public player-identity resolution for list/detail read paths (issue #29) so enriching N
/// rows costs one query, not N - see <see cref="IPlayerProfileStore.FindByIdsAsync"/>. Best-effort:
/// the returned dictionary contains only the ids that actually resolved, exactly like
/// <see cref="IPlayerProfileStore.FindByIdsAsync"/> itself - a miss means the referencing row and
/// its FK-backed profile have gone out of sync (see PR #24), and it is each caller's decision
/// whether that should drop just the affected row (list endpoints, matching
/// <see cref="Level5.Application.Social.ListFriendsUseCase"/>'s established pattern) or fail the
/// whole response (single-resource reads, e.g. <see cref="Level5.Application.Competition.GetSeriesUseCase"/>).
/// </summary>
internal static class PublicPlayerSummaries
{
    public static async Task<IReadOnlyDictionary<PlayerId, PublicPlayerSummary>> ResolveAsync(
        IPlayerProfileStore playerProfileStore, IReadOnlyCollection<PlayerId> playerIds, CancellationToken cancellationToken)
    {
        var distinctIds = playerIds.Distinct().ToArray();
        if (distinctIds.Length == 0)
        {
            return new Dictionary<PlayerId, PublicPlayerSummary>();
        }

        var profiles = await playerProfileStore.FindByIdsAsync(distinctIds, cancellationToken);
        return profiles.ToDictionary(p => p.Id, p => new PublicPlayerSummary(p.Id, p.DisplayName, p.Tag.Value));
    }
}
