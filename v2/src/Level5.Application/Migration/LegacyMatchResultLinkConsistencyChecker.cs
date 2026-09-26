using Level5.Application.Abstractions;
using Level5.Domain.Migration;

namespace Level5.Application.Migration;

/// <summary>
/// The single definition of "is this legacy_match_result_links row consistent" - shared by
/// <see cref="ImportLegacyMatchResultUseCase"/>'s idempotent-resume check and the standalone
/// <c>verify</c> command, so the two can never define consistency differently (mirrors
/// <see cref="LegacyLinkConsistencyChecker"/>'s own role for account migration).
/// </summary>
public static class LegacyMatchResultLinkConsistencyChecker
{
    public static async Task<LegacyLinkConsistencyResult> CheckAsync(
        LegacyMatchResultLink link,
        int legacyUserId,
        LegacyHighscoreMappingInput currentSourceRow,
        IMatchResultStore matchResultStore,
        ILegacyAccountLinkStore legacyAccountLinkStore,
        ILeaderboardPolicyCatalog leaderboardPolicyCatalog,
        CancellationToken cancellationToken)
    {
        var result = await matchResultStore.FindByIdAsync(link.MatchResultId, cancellationToken);
        if (result is null)
        {
            return new LegacyLinkConsistencyResult(false,
                $"legacy_match_result_links row for legacy highscore {link.LegacyHighscoreId} points to MatchResult {link.MatchResultId}, but that result does not exist.");
        }

        var accountLink = await legacyAccountLinkStore.FindByLegacyUserIdAsync(legacyUserId, cancellationToken);
        if (accountLink is null)
        {
            return new LegacyLinkConsistencyResult(false,
                $"legacy_match_result_links row for legacy highscore {link.LegacyHighscoreId} has no resolvable legacy_account_links row for legacy user {legacyUserId}.");
        }

        if (result.PlayerId != accountLink.PlayerId)
        {
            return new LegacyLinkConsistencyResult(false,
                $"legacy_match_result_links row for legacy highscore {link.LegacyHighscoreId} points to MatchResult {link.MatchResultId} owned by PlayerId {result.PlayerId}, " +
                $"but legacy_account_links currently resolves legacy user {legacyUserId} to PlayerId {accountLink.PlayerId}.");
        }

        var mapping = LegacyHighscoreMapper.TryMap(currentSourceRow);
        if (!mapping.IsValid)
        {
            return new LegacyLinkConsistencyResult(false,
                $"legacy_match_result_links row for legacy highscore {link.LegacyHighscoreId} no longer maps cleanly from its V1 source row: {mapping.BlockDetail}");
        }

        var sameRequest = result.ClientResultId == mapping.ClientResultId
            && result.MatchesRequest(
                mapping.ModeId, mapping.LevelId, mapping.CharacterId!, mapping.ClientVersion!, mapping.Platform!,
                mapping.Metrics!, mapping.Modifiers!);
        if (!sameRequest)
        {
            return new LegacyLinkConsistencyResult(false,
                $"legacy_match_result_links row for legacy highscore {link.LegacyHighscoreId} points to MatchResult {link.MatchResultId}, " +
                "but its persisted fields no longer match the canonical mapping of its V1 source row.");
        }

        var policy = leaderboardPolicyCatalog.TryResolve(mapping.ModeId);
        if (policy is not null && result.Metrics.ValueOf(policy.RankingMetric) is null)
        {
            return new LegacyLinkConsistencyResult(false,
                $"legacy_match_result_links row for legacy highscore {link.LegacyHighscoreId} maps to mode {mapping.ModeId}, " +
                $"which requires the '{policy.RankingMetric}' ranking metric, but MatchResult {link.MatchResultId} does not carry it.");
        }

        return new LegacyLinkConsistencyResult(true, null);
    }
}
