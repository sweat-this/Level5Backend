using Level5.Application.Abstractions;
using Level5.Domain.Leaderboards;
using Level5.Domain.Results;

namespace Level5.Infrastructure.Leaderboards;

/// <summary>
/// The server's authoritative leaderboard policy catalog: hardcoded and in-memory, mirroring the
/// legacy V1 <c>HighscoresApiController.GetScoreMetric</c> mode-id-to-metric grouping exactly (the
/// audited mapping this build carries forward), minus the modes V1 never recognized. Mode 27
/// (Lockdown) is deliberately absent - Unity authors it with a Total Points high-score field, but
/// V1's online leaderboard mapping never included it, and that discrepancy is not resolved here.
/// Modes 98 (Arcade) and 99 (Free Play) are likewise absent - V1 never gave them a score metric
/// either.
/// </summary>
public sealed class StaticLeaderboardPolicyCatalog : ILeaderboardPolicyCatalog
{
    private static readonly IReadOnlyDictionary<int, LeaderboardPolicy> Entries = BuildEntries();

    public LeaderboardPolicy? TryResolve(int modeId) => Entries.GetValueOrDefault(modeId);

    private static IReadOnlyDictionary<int, LeaderboardPolicy> BuildEntries()
    {
        var entries = new Dictionary<int, LeaderboardPolicy>();

        void Add(MatchResultMetric metric, RankingDirection direction, params int[] modeIds)
        {
            foreach (var modeId in modeIds)
            {
                entries.Add(modeId, new LeaderboardPolicy(modeId, metric, direction));
            }
        }

        Add(MatchResultMetric.TotalPoints, RankingDirection.HigherWins, 1, 15, 16, 17, 18, 19, 23, 24, 26);
        Add(MatchResultMetric.ShotsMade, RankingDirection.HigherWins, 2, 3, 4);
        Add(MatchResultMetric.TotalDistance, RankingDirection.HigherWins, 6);
        Add(MatchResultMetric.CompletionTimeSeconds, RankingDirection.LowerWins, 7, 8, 9, 25);
        Add(MatchResultMetric.LongestStreak, RankingDirection.HigherWins, 14);
        Add(MatchResultMetric.EnemiesKilled, RankingDirection.HigherWins, 20, 21, 22);

        return entries;
    }
}
