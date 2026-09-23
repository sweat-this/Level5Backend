using Level5.Application.Leaderboards;
using Level5.Application.Players;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Level5.Api.Controllers;

public sealed record LeaderboardEntryDto(
    Guid MatchResultId, PublicPlayerSummaryDto Player, string CharacterId, int LevelId,
    double Value, DateTimeOffset CreatedAt, MatchResultModifiersDto Modifiers);

/// <summary>
/// The bounded-pagination envelope <c>GET /api/v2/leaderboards/{modeId}</c> returns. <see cref="Metric"/>
/// and <see cref="Direction"/> are server-resolved (from <c>ILeaderboardPolicyCatalog</c>) so a
/// client can render what it is looking at without ever choosing the ranking metric or sort
/// direction itself. <see cref="NextCursor"/> is <c>null</c> once the caller has reached the end of
/// the result set, and must be passed back verbatim (never parsed) as the next request's <c>cursor</c>.
/// </summary>
public sealed record LeaderboardPageDto(
    int ModeId, string Metric, string Direction, IReadOnlyList<LeaderboardEntryDto> Items, int Limit, string? NextCursor);

/// <summary>
/// Server-authoritative leaderboard reads over <c>match_results</c> (issue: leaderboard reads).
/// The client chooses a mode id, optional modifier filters, and pagination - it never chooses the
/// ranking metric, sort direction, or any wire/database field name.
/// </summary>
[ApiController]
[Route("api/v2/leaderboards")]
[Authorize]
public sealed class LeaderboardsController(GetLeaderboardUseCase getLeaderboard) : ControllerBase
{
    [HttpGet("{modeId:int}")]
    public async Task<ActionResult<LeaderboardPageDto>> Get(
        int modeId, int? limit, string? cursor, bool? hardcore, bool? traffic, bool? enemies, bool? sniper,
        CancellationToken cancellationToken)
    {
        var page = await getLeaderboard.ExecuteAsync(
            new GetLeaderboardRequest(modeId, hardcore, traffic, enemies, sniper, limit, cursor), cancellationToken);

        return Ok(ToDto(page, LeaderboardPaging.ResolveLimit(limit)));
    }

    private static LeaderboardPageDto ToDto(LeaderboardPage page, int resolvedLimit) => new(
        page.ModeId, page.Metric.ToString(), page.Direction.ToString(),
        [.. page.Items.Select(ToDto)], resolvedLimit, page.NextCursor);

    private static LeaderboardEntryDto ToDto(LeaderboardEntryView entry) => new(
        entry.MatchResultId, new PublicPlayerSummaryDto(entry.Player.PlayerId.Value, entry.Player.DisplayName, entry.Player.Tag),
        entry.CharacterId, entry.LevelId, entry.Value, entry.CreatedAt,
        new MatchResultModifiersDto(entry.Modifiers.Hardcore, entry.Modifiers.TrafficEnabled, entry.Modifiers.EnemiesEnabled, entry.Modifiers.SniperEnabled));
}
