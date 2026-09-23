using Level5.Domain.Results;

namespace Level5.Domain.Leaderboards;

/// <summary>
/// The server-owned ranking rule for one <see cref="MatchResult.ModeId"/>: which
/// <see cref="MatchResultMetric"/> ranks the board, and which direction ranks better. Deliberately
/// separate from correspondence's <see cref="Level5.Domain.Competition.FrozenRules"/>/
/// <see cref="Level5.Domain.Competition.ComparisonKey"/> - those govern one frozen series' own
/// comparison sequence; this governs an ordinary match result's general leaderboard eligibility,
/// and the two must be free to evolve independently.
/// </summary>
public sealed record LeaderboardPolicy(int ModeId, MatchResultMetric RankingMetric, RankingDirection Direction);
