using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Players;
using Level5.Domain.Ids;
using Level5.Domain.Leaderboards;
using Level5.Domain.Results;

namespace Level5.Application.Leaderboards;

public sealed record GetLeaderboardRequest(
    int ModeId, bool? Hardcore, bool? TrafficEnabled, bool? EnemiesEnabled, bool? SniperEnabled, int? Limit, string? Cursor);

public sealed record LeaderboardEntryView(
    Guid MatchResultId, PublicPlayerSummary Player, string CharacterId, int LevelId,
    double Value, DateTimeOffset CreatedAt, MatchResultModifiers Modifiers);

/// <summary>
/// One bounded page of a mode's leaderboard, plus the server-resolved ranking policy that produced
/// it (issue: leaderboard reads) - a client reads <see cref="Metric"/>/<see cref="Direction"/> back
/// but never supplies them; they come only from <see cref="ILeaderboardPolicyCatalog"/>.
/// </summary>
public sealed record LeaderboardPage(
    int ModeId, MatchResultMetric Metric, RankingDirection Direction, IReadOnlyList<LeaderboardEntryView> Items, string? NextCursor);

/// <summary>
/// Resolves a mode's server-owned leaderboard policy, then delegates ranking to
/// <see cref="ILeaderboardQuery"/> - this use case never ranks in memory itself. The pagination
/// cursor's scope is derived from the mode id plus every active modifier filter, so a cursor
/// issued for one board/filter combination is rejected outright (400) if replayed against another
/// (issue: leaderboard reads).
/// </summary>
public sealed class GetLeaderboardUseCase(ILeaderboardPolicyCatalog policyCatalog, ILeaderboardQuery query)
{
    public async Task<LeaderboardPage> ExecuteAsync(GetLeaderboardRequest request, CancellationToken cancellationToken)
    {
        var policy = policyCatalog.TryResolve(request.ModeId)
            ?? throw new UnsupportedLeaderboardModeException($"Mode {request.ModeId} does not have a leaderboard.");

        var resolvedLimit = LeaderboardPaging.ResolveLimit(request.Limit);
        var scope = BuildScope(request, policy);
        var cursor = request.Cursor is null ? default((double, DateTimeOffset, Guid)?) : LeaderboardCursor.Decode(scope, request.Cursor);

        var spec = new LeaderboardQuerySpec(
            request.ModeId, policy.RankingMetric, policy.Direction,
            request.Hardcore, request.TrafficEnabled, request.EnemiesEnabled, request.SniperEnabled,
            resolvedLimit, cursor);

        var rows = await query.ExecuteAsync(spec, cancellationToken);

        var hasMore = rows.Count > resolvedLimit;
        var page = hasMore ? [.. rows.Take(resolvedLimit)] : rows;

        var items = page.Select(r => new LeaderboardEntryView(
            r.MatchResultId,
            new PublicPlayerSummary(new PlayerId(r.PlayerId), r.DisplayName, r.Tag),
            r.CharacterId, r.LevelId, r.RankingValue, r.CreatedAt,
            MatchResultModifiers.Of(r.Hardcore, r.TrafficEnabled, r.EnemiesEnabled, r.SniperEnabled))).ToList();

        var nextCursor = hasMore
            ? LeaderboardCursor.Encode(scope, page[^1].RankingValue, page[^1].CreatedAt, page[^1].MatchResultId)
            : null;

        return new LeaderboardPage(request.ModeId, policy.RankingMetric, policy.Direction, items, nextCursor);
    }

    /// <summary>
    /// Everything that defines "which ordered row set is this cursor a position within": the mode,
    /// the resolved ranking policy, and the modifier filter state. The policy is included even
    /// though it is currently derivable from the mode id - if the catalog's mapping for a mode ever
    /// changes, a cursor minted under the old policy must be rejected rather than silently applied
    /// to a different ranking metric or sort direction.
    ///
    /// "|"-separated, not ":" - <see cref="LeaderboardCursor"/>'s own encoding uses ":" to frame
    /// scope/rankingValue/createdAt/id, so a scope containing ":" would corrupt that framing.
    /// </summary>
    private static string BuildScope(GetLeaderboardRequest request, LeaderboardPolicy policy) =>
        $"leaderboard|{request.ModeId}|{policy.RankingMetric}|{policy.Direction}|" +
        $"{Fmt(request.Hardcore)}|{Fmt(request.TrafficEnabled)}|{Fmt(request.EnemiesEnabled)}|{Fmt(request.SniperEnabled)}";

    private static string Fmt(bool? value) => value?.ToString() ?? "null";
}
