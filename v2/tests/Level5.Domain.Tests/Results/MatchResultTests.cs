using Level5.Domain.Ids;
using Level5.Domain.Results;
using Xunit;

namespace Level5.Domain.Tests.Results;

public class MatchResultTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static MatchResultMetrics DefaultMetrics(double totalPoints = 90) =>
        MatchResultMetrics.Of(new Dictionary<MatchResultMetric, double> { [MatchResultMetric.TotalPoints] = totalPoints });

    private static readonly MatchResultModifiers DefaultModifiers = MatchResultModifiers.Of(false, false, false, false);

    private static MatchResult ValidResult(
        PlayerId? playerId = null, Guid? clientResultId = null, string modeId = "arcade", string levelId = "level-1",
        string characterId = "hero", string clientVersion = "1.0.0", string platform = "ios",
        MatchResultMetrics? metrics = null, MatchResultModifiers? modifiers = null)
        => MatchResult.Submit(
            playerId ?? PlayerId.New(), clientResultId ?? Guid.NewGuid(), modeId, levelId, characterId,
            clientVersion, platform, metrics ?? DefaultMetrics(), modifiers ?? DefaultModifiers, Now);

    [Fact]
    public void Submit_creates_a_valid_result()
    {
        var playerId = PlayerId.New();
        var clientResultId = Guid.NewGuid();

        var result = ValidResult(playerId, clientResultId);

        Assert.Equal(playerId, result.PlayerId);
        Assert.Equal(clientResultId, result.ClientResultId);
        Assert.Equal(Now, result.CreatedAt);
        Assert.NotEqual(default, result.Id.Value);
    }

    [Fact]
    public void Submit_rejects_an_empty_clientResultId()
    {
        Assert.Throws<InvalidMatchResultException>(() => ValidResult(clientResultId: Guid.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Submit_rejects_an_empty_modeId(string modeId)
    {
        Assert.Throws<InvalidMatchResultException>(() => ValidResult(modeId: modeId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Submit_rejects_an_empty_levelId(string levelId)
    {
        Assert.Throws<InvalidMatchResultException>(() => ValidResult(levelId: levelId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Submit_rejects_an_empty_characterId(string characterId)
    {
        Assert.Throws<InvalidMatchResultException>(() => ValidResult(characterId: characterId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Submit_rejects_an_empty_clientVersion(string clientVersion)
    {
        Assert.Throws<InvalidMatchResultException>(() => ValidResult(clientVersion: clientVersion));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Submit_rejects_an_empty_platform(string platform)
    {
        Assert.Throws<InvalidMatchResultException>(() => ValidResult(platform: platform));
    }

    [Fact]
    public void Two_distinct_submissions_get_distinct_ids()
    {
        var a = ValidResult();
        var b = ValidResult();

        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void MatchesRequest_is_true_for_an_identical_replay()
    {
        var result = ValidResult(modeId: "arcade", levelId: "level-1", characterId: "hero", clientVersion: "1.0.0", platform: "ios");

        var same = result.MatchesRequest("arcade", "level-1", "hero", "1.0.0", "ios", DefaultMetrics(), DefaultModifiers);

        Assert.True(same);
    }

    [Fact]
    public void MatchesRequest_is_false_when_metrics_differ()
    {
        var result = ValidResult(metrics: DefaultMetrics(90));

        var same = result.MatchesRequest("arcade", "level-1", "hero", "1.0.0", "ios", DefaultMetrics(91), DefaultModifiers);

        Assert.False(same);
    }

    [Fact]
    public void MatchesRequest_is_false_when_modifiers_differ()
    {
        var result = ValidResult(modifiers: DefaultModifiers);

        var same = result.MatchesRequest(
            "arcade", "level-1", "hero", "1.0.0", "ios", DefaultMetrics(),
            MatchResultModifiers.Of(true, false, false, false));

        Assert.False(same);
    }

    [Theory]
    [InlineData("other-mode", "level-1", "hero", "1.0.0", "ios")]
    [InlineData("arcade", "other-level", "hero", "1.0.0", "ios")]
    [InlineData("arcade", "level-1", "other-hero", "1.0.0", "ios")]
    [InlineData("arcade", "level-1", "hero", "2.0.0", "ios")]
    [InlineData("arcade", "level-1", "hero", "1.0.0", "android")]
    public void MatchesRequest_is_false_when_any_client_field_differs(
        string modeId, string levelId, string characterId, string clientVersion, string platform)
    {
        var result = ValidResult(modeId: "arcade", levelId: "level-1", characterId: "hero", clientVersion: "1.0.0", platform: "ios");

        var same = result.MatchesRequest(modeId, levelId, characterId, clientVersion, platform, DefaultMetrics(), DefaultModifiers);

        Assert.False(same);
    }

    [Fact]
    public void Rehydrate_reconstitutes_every_field()
    {
        var id = MatchResultId.New();
        var playerId = PlayerId.New();
        var clientResultId = Guid.NewGuid();
        var metrics = DefaultMetrics(42);
        var modifiers = MatchResultModifiers.Of(true, true, false, false);

        var result = MatchResult.Rehydrate(
            id, playerId, clientResultId, "mode", "level", "character", "1.2.3", "pc", metrics, modifiers, Now);

        Assert.Equal(id, result.Id);
        Assert.Equal(playerId, result.PlayerId);
        Assert.Equal(clientResultId, result.ClientResultId);
        Assert.Equal("mode", result.ModeId);
        Assert.Equal("level", result.LevelId);
        Assert.Equal("character", result.CharacterId);
        Assert.Equal("1.2.3", result.ClientVersion);
        Assert.Equal("pc", result.Platform);
        Assert.Equal(metrics, result.Metrics);
        Assert.Equal(modifiers, result.Modifiers);
        Assert.Equal(Now, result.CreatedAt);
    }
}
