using Level5.Domain.Leaderboards;
using Level5.Domain.Results;

namespace Level5.Application.Abstractions;

/// <summary>
/// Ranks <c>match_results</c> directly in PostgreSQL for one leaderboard read - never loads every
/// candidate row and sorts in memory, and never hydrates full <see cref="MatchResult"/> aggregates
/// (issue: leaderboard reads). Deliberately separate from <see cref="IMatchResultStore"/>, which
/// stays the command/raw-result persistence boundary and is not expanded into a search/query
/// repository.
/// </summary>
public interface ILeaderboardQuery
{
    /// <summary>
    /// Returns up to <see cref="LeaderboardQuerySpec.Limit"/> + 1 rows (the extra row is how the
    /// caller detects whether a next page exists, matching <c>IVersusSeriesStore</c>'s established
    /// pagination convention), ordered per <see cref="LeaderboardQuerySpec.Direction"/> with a
    /// deterministic tiebreak. A row whose <see cref="LeaderboardQuerySpec.RankingMetric"/> is
    /// absent from its persisted metrics is excluded defensively, rather than failing the query.
    /// </summary>
    Task<IReadOnlyList<LeaderboardRow>> ExecuteAsync(LeaderboardQuerySpec spec, CancellationToken cancellationToken);
}

public sealed record LeaderboardQuerySpec(
    int ModeId,
    MatchResultMetric RankingMetric,
    RankingDirection Direction,
    bool? Hardcore,
    bool? TrafficEnabled,
    bool? EnemiesEnabled,
    bool? SniperEnabled,
    int Limit,
    (double RankingValue, DateTimeOffset CreatedAt, Guid MatchResultId)? Cursor);

/// <summary>One ranked row, already joined to the submitting player's public identity - never AccountId/Username/Email.</summary>
public sealed record LeaderboardRow(
    Guid MatchResultId,
    Guid PlayerId,
    string DisplayName,
    string Tag,
    string CharacterId,
    int LevelId,
    double RankingValue,
    DateTimeOffset CreatedAt,
    bool Hardcore,
    bool TrafficEnabled,
    bool EnemiesEnabled,
    bool SniperEnabled);
