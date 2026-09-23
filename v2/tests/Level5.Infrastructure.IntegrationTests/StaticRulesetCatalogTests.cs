using Level5.Application.Abstractions;
using Level5.Application.Competition;
using Level5.Domain.Competition;
using Level5.Infrastructure.Competition;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

/// <summary>
/// Regression coverage for the "most-points" entry added during issue #159 live Unity-client
/// certification: "score-only" (this catalog's only entry until then) has no counterpart in the
/// Unity client's own ruleset registry (<c>DefaultCompetitiveRulesets</c>), so
/// <c>RemoteAttemptDescriptorMapper.Map</c> could never resolve it and no client build could ever
/// actually launch a match for the server's only advertised ruleset - contrary to this catalog's own
/// doc comment. "most-points" mirrors the real, already-shipped Unity ruleset of the same id
/// exactly (<c>DefaultCompetitiveRulesets.Score("most-points", GameModeId.TotalPoints, ...)</c>).
/// </summary>
public sealed class StaticRulesetCatalogTests
{
    private readonly StaticRulesetCatalog _catalog = new();

    [Fact]
    public void Resolves_most_points_as_a_sealed_single_metric_ruleset()
    {
        RulesetDefinition definition = _catalog.Resolve("most-points", requestedVersion: null);

        Assert.Equal("most-points", definition.RulesetId);
        Assert.Equal(1, definition.RulesetVersion);
        Assert.Equal(1, definition.MinimumCompatibleVersion);
        Assert.Equal(InformationPolicy.SealedAttempt, definition.InformationPolicy);
        Assert.False(definition.AlternatesFirstAttempt);
        ComparisonKey key = Assert.Single(definition.ComparisonKeys);
        Assert.Equal(ResultMetric.Score, key.Metric);
        Assert.Equal(MetricDirection.HigherWins, key.Direction);
    }

    [Fact]
    public void Still_resolves_score_only_unchanged()
    {
        RulesetDefinition definition = _catalog.Resolve("score-only", requestedVersion: null);

        Assert.Equal("score-only", definition.RulesetId);
        Assert.Equal("mode-score-only", definition.ModeId);
    }

    [Fact]
    public void Rejects_an_unrecognized_ruleset_id()
    {
        Assert.Throws<UnknownRulesetException>(() => _catalog.Resolve("not-a-real-ruleset", requestedVersion: null));
    }
}
