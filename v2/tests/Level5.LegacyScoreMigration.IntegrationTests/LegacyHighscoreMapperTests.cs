using Level5.Application.Migration;
using Level5.Domain.Results;

namespace Level5.LegacyScoreMigration.IntegrationTests;

/// <summary>Pure unit tests - no database needed, mirrors LegacyCredentialClassifierTests's placement in the sibling account-migration test project.</summary>
public sealed class LegacyHighscoreMapperTests
{
    private static readonly Guid FixedScoreGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static LegacyHighscoreMappingInput Valid(
        string? scoreid = null, int modeid = 1, int levelid = 1, int characterid = 5,
        string? version = "1.4.2", string? platform = "Handheld",
        int totalPoints = 120, int maxShotMade = 8, float totalDistance = 42.5f, float time = 30.1f,
        int consecutiveShots = 4, int enemiesKilled = 2,
        int hardcoreEnabled = 0, int trafficEnabled = 0, int enemiesEnabled = 0, int sniperEnabled = 0) => new(
        scoreid ?? FixedScoreGuid.ToString("N"), modeid, levelid, characterid, version, platform,
        totalPoints, maxShotMade, totalDistance, time, consecutiveShots, enemiesKilled,
        hardcoreEnabled, trafficEnabled, enemiesEnabled, sniperEnabled);

    [Fact]
    public void A_valid_scoreid_maps_exactly_to_the_client_result_id()
    {
        var result = LegacyHighscoreMapper.TryMap(Valid(scoreid: FixedScoreGuid.ToString("N")));

        Assert.True(result.IsValid);
        Assert.Equal(FixedScoreGuid, result.ClientResultId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void A_missing_or_malformed_scoreid_blocks(string? scoreid)
    {
        // Constructed directly rather than through Valid()'s optional-parameter default: `??`
        // cannot distinguish "caller wants the default" from "caller explicitly passed null".
        var input = new LegacyHighscoreMappingInput(
            scoreid, 1, 1, 5, "1.4.2", "Handheld", 100, 8, 42.5f, 30.1f, 4, 2, 0, 0, 0, 0);

        var result = LegacyHighscoreMapper.TryMap(input);

        Assert.False(result.IsValid);
        Assert.Equal(LegacyHighscoreMappingBlocker.MissingOrMalformedScoreid, result.Blocker);
    }

    [Theory]
    [InlineData("N")]
    [InlineData("D")]
    [InlineData("B")]
    public void Guid_TryParse_accepts_every_format_unity_historically_generates_or_could_have(string format)
    {
        // Guid.TryParse accepts "N" (no dashes, Unity's actual generateUniqueScoreID() format),
        // "D" (dashed), and "B" (braced) - and is case-insensitive - so every realistic historical
        // variant maps identically, never treated as a format-specific special case.
        var scoreid = FixedScoreGuid.ToString(format).ToUpperInvariant();

        var result = LegacyHighscoreMapper.TryMap(Valid(scoreid: scoreid));

        Assert.True(result.IsValid);
        Assert.Equal(FixedScoreGuid, result.ClientResultId);
    }

    [Fact]
    public void MaxShotMade_maps_to_the_ShotsMade_metric()
    {
        var result = LegacyHighscoreMapper.TryMap(Valid(maxShotMade: 17));

        Assert.Equal(17d, result.Metrics!.ValueOf(MatchResultMetric.ShotsMade));
    }

    [Fact]
    public void All_six_metrics_map_exactly()
    {
        var result = LegacyHighscoreMapper.TryMap(Valid(
            totalPoints: 100, maxShotMade: 8, totalDistance: 42.5f, time: 30.1f, consecutiveShots: 4, enemiesKilled: 2));

        Assert.True(result.IsValid);
        Assert.Equal(100d, result.Metrics!.ValueOf(MatchResultMetric.TotalPoints));
        Assert.Equal(8d, result.Metrics.ValueOf(MatchResultMetric.ShotsMade));
        Assert.Equal(42.5d, result.Metrics.ValueOf(MatchResultMetric.TotalDistance)!.Value, 3);
        Assert.Equal(30.1d, result.Metrics.ValueOf(MatchResultMetric.CompletionTimeSeconds)!.Value, 3);
        Assert.Equal(4d, result.Metrics.ValueOf(MatchResultMetric.LongestStreak));
        Assert.Equal(2d, result.Metrics.ValueOf(MatchResultMetric.EnemiesKilled));
    }

    [Fact]
    public void Migration_never_populates_unsupported_v1_telemetry_into_unrelated_fields()
    {
        // LegacyHighscoreMappingInput structurally carries only the six mapped metrics - there is no
        // slot for longest shot, shot-attempt counts, point breakdowns, sniper details, or P1-P4
        // fields to leak into, so the mapped result can only ever contain exactly these six keys.
        var result = LegacyHighscoreMapper.TryMap(Valid());

        Assert.True(result.IsValid);
        Assert.Equal(6, result.Metrics!.Values.Count);
    }

    [Fact]
    public void All_four_modifiers_map_with_not_equal_zero()
    {
        var result = LegacyHighscoreMapper.TryMap(Valid(hardcoreEnabled: 1, trafficEnabled: 2, enemiesEnabled: 0, sniperEnabled: -1));

        Assert.True(result.IsValid);
        Assert.True(result.Modifiers!.Hardcore);
        Assert.True(result.Modifiers.TrafficEnabled);
        Assert.False(result.Modifiers.EnemiesEnabled);
        Assert.True(result.Modifiers.SniperEnabled);
    }

    [Fact]
    public void A_valid_positive_mode_with_no_leaderboard_policy_still_maps_cleanly()
    {
        // Mode 27 (Lockdown) has no StaticLeaderboardPolicyCatalog entry, but the mapper itself
        // never consults the leaderboard catalog - a positive mode id always maps, regardless of
        // whether it happens to have a leaderboard.
        var result = LegacyHighscoreMapper.TryMap(Valid(modeid: 27));

        Assert.True(result.IsValid);
        Assert.Equal(27, result.ModeId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_mode_blocks(int modeid)
    {
        var result = LegacyHighscoreMapper.TryMap(Valid(modeid: modeid));

        Assert.False(result.IsValid);
        Assert.Equal(LegacyHighscoreMappingBlocker.NonPositiveMode, result.Blocker);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_level_blocks(int levelid)
    {
        var result = LegacyHighscoreMapper.TryMap(Valid(levelid: levelid));

        Assert.False(result.IsValid);
        Assert.Equal(LegacyHighscoreMappingBlocker.NonPositiveLevel, result.Blocker);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("this-version-string-is-definitely-longer-than-thirty-two-characters")]
    public void An_invalid_or_oversized_version_blocks(string? version)
    {
        var result = LegacyHighscoreMapper.TryMap(Valid(version: version));

        Assert.False(result.IsValid);
        Assert.Equal(LegacyHighscoreMappingBlocker.InvalidVersion, result.Blocker);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("this-platform-string-is-definitely-longer-than-thirty-two-characters")]
    public void An_invalid_missing_or_oversized_platform_blocks(string? platform)
    {
        var result = LegacyHighscoreMapper.TryMap(Valid(platform: platform));

        Assert.False(result.IsValid);
        Assert.Equal(LegacyHighscoreMappingBlocker.InvalidPlatform, result.Blocker);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-1f)]
    public void An_invalid_metric_value_blocks(float badTotalDistance)
    {
        var result = LegacyHighscoreMapper.TryMap(Valid(totalDistance: badTotalDistance));

        Assert.False(result.IsValid);
        Assert.Equal(LegacyHighscoreMappingBlocker.InvalidMetricValue, result.Blocker);
    }
}
