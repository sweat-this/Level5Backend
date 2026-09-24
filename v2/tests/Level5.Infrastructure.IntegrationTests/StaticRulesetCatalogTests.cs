using Level5.Application.Competition;
using Level5.Domain.Competition;
using Level5.Infrastructure.Competition;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

/// <summary>
/// Pins the production remote ruleset catalog's exact wire semantics - not just that a lookup
/// succeeds. Requires no database; runs alongside the other Infrastructure tests only because
/// <c>StaticRulesetCatalog</c> lives in Infrastructure.
///
/// "most-points" is the one catalog entry Unity dev's own ruleset registry
/// (<c>DefaultCompetitiveRulesets.Score("most-points", GameModeId.TotalPoints, ...)</c>) can
/// actually resolve and launch through <c>RemoteAttemptDescriptorMapper</c> - "score-only" has no
/// Unity counterpart at all. A silent change to "most-points"'s id, version, or ordered
/// comparison keys/directions here would desynchronize from Unity's own frozen definition under
/// the same RulesetId+RulesetVersion without either side's own tests catching it, since each side
/// only tests against its own copy of the rules.
/// </summary>
public sealed class StaticRulesetCatalogTests
{
    private readonly StaticRulesetCatalog _catalog = new();

    [Fact]
    public void MostPoints_resolves_to_the_exact_definition_unity_dev_ships()
    {
        var definition = _catalog.Resolve("most-points", requestedVersion: null);

        Assert.Equal("most-points", definition.RulesetId);
        Assert.Equal(1, definition.RulesetVersion);
        Assert.Equal(1, definition.MinimumCompatibleVersion);
        Assert.Equal(InformationPolicy.SealedAttempt, definition.InformationPolicy);
        Assert.False(definition.AlternatesFirstAttempt);

        // Order and direction matter: this mirrors Unity dev's DefaultCompetitiveRulesets.Score(),
        // whose own doc comment ("better accuracy breaks a tie, then fewer attempts") is the
        // authority for this exact tie-break order - not a value this test may casually "fix".
        Assert.Equal(
            [
                new ComparisonKey(ResultMetric.Score, MetricDirection.HigherWins),
                new ComparisonKey(ResultMetric.Accuracy, MetricDirection.HigherWins),
                new ComparisonKey(ResultMetric.ShotsAttempted, MetricDirection.LowerWins)
            ],
            definition.ComparisonKeys);
    }

    [Fact]
    public void MostPoints_version_1_is_within_its_own_supported_range()
    {
        var definition = _catalog.Resolve("most-points", requestedVersion: 1);

        Assert.Equal(1, definition.RulesetVersion);
    }

    [Fact]
    public void ScoreOnly_is_unchanged_by_adding_most_points()
    {
        var definition = _catalog.Resolve("score-only", requestedVersion: null);

        Assert.Equal("score-only", definition.RulesetId);
        Assert.Equal(1, definition.RulesetVersion);
        Assert.Equal("mode-score-only", definition.ModeId);
        Assert.Equal(
            [new ComparisonKey(ResultMetric.Score, MetricDirection.HigherWins)],
            definition.ComparisonKeys);
    }

    [Fact]
    public void Unknown_ruleset_still_fails_deterministically()
    {
        Assert.Throws<UnknownRulesetException>(() => _catalog.Resolve("does-not-exist", requestedVersion: null));
    }
}
