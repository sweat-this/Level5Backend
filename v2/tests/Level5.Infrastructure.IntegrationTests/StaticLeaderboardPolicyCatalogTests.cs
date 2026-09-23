using Level5.Domain.Leaderboards;
using Level5.Domain.Results;
using Level5.Infrastructure.Leaderboards;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

/// <summary>
/// Asserts the audited V1-parity mode-id-to-metric mapping directly, independent of Postgres -
/// mirrors legacy <c>HighscoresApiController.GetScoreMetric</c> exactly, minus the modes V1 never
/// recognized (27, 98, 99).
/// </summary>
public sealed class StaticLeaderboardPolicyCatalogTests
{
    private readonly StaticLeaderboardPolicyCatalog _catalog = new();

    [Theory]
    [InlineData(1, MatchResultMetric.TotalPoints, RankingDirection.HigherWins)]
    [InlineData(2, MatchResultMetric.ShotsMade, RankingDirection.HigherWins)]
    [InlineData(3, MatchResultMetric.ShotsMade, RankingDirection.HigherWins)]
    [InlineData(4, MatchResultMetric.ShotsMade, RankingDirection.HigherWins)]
    [InlineData(6, MatchResultMetric.TotalDistance, RankingDirection.HigherWins)]
    [InlineData(7, MatchResultMetric.CompletionTimeSeconds, RankingDirection.LowerWins)]
    [InlineData(8, MatchResultMetric.CompletionTimeSeconds, RankingDirection.LowerWins)]
    [InlineData(9, MatchResultMetric.CompletionTimeSeconds, RankingDirection.LowerWins)]
    [InlineData(14, MatchResultMetric.LongestStreak, RankingDirection.HigherWins)]
    [InlineData(15, MatchResultMetric.TotalPoints, RankingDirection.HigherWins)]
    [InlineData(16, MatchResultMetric.TotalPoints, RankingDirection.HigherWins)]
    [InlineData(17, MatchResultMetric.TotalPoints, RankingDirection.HigherWins)]
    [InlineData(18, MatchResultMetric.TotalPoints, RankingDirection.HigherWins)]
    [InlineData(19, MatchResultMetric.TotalPoints, RankingDirection.HigherWins)]
    [InlineData(20, MatchResultMetric.EnemiesKilled, RankingDirection.HigherWins)]
    [InlineData(21, MatchResultMetric.EnemiesKilled, RankingDirection.HigherWins)]
    [InlineData(22, MatchResultMetric.EnemiesKilled, RankingDirection.HigherWins)]
    [InlineData(23, MatchResultMetric.TotalPoints, RankingDirection.HigherWins)]
    [InlineData(24, MatchResultMetric.TotalPoints, RankingDirection.HigherWins)]
    [InlineData(25, MatchResultMetric.CompletionTimeSeconds, RankingDirection.LowerWins)]
    [InlineData(26, MatchResultMetric.TotalPoints, RankingDirection.HigherWins)]
    public void Resolves_the_audited_V1_parity_policy_for_every_supported_mode(int modeId, MatchResultMetric metric, RankingDirection direction)
    {
        var policy = _catalog.TryResolve(modeId);

        Assert.NotNull(policy);
        Assert.Equal(modeId, policy.ModeId);
        Assert.Equal(metric, policy.RankingMetric);
        Assert.Equal(direction, policy.Direction);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(27)] // Lockdown - authored with a Total Points high-score field in Unity, but never in V1's online leaderboard mapping.
    [InlineData(98)] // Arcade
    [InlineData(99)] // Free Play
    [InlineData(123456)] // unknown numeric id
    public void Has_no_policy_for_an_unsupported_mode(int modeId)
    {
        Assert.Null(_catalog.TryResolve(modeId));
    }
}
