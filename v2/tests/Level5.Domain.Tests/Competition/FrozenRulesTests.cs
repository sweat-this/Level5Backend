using Level5.Domain.Competition;
using Xunit;

namespace Level5.Domain.Tests.Competition;

public class FrozenRulesTests
{
    private static readonly ComparisonKey[] DefaultKeys = [new(ResultMetric.Score, MetricDirection.HigherWins)];

    private static FrozenRules CreateDefault(
        string rulesetId = "score-only", int rulesetVersion = 1, int minimumCompatibleVersion = 1,
        string modeId = "mode-score-only", ComparisonKey[]? comparisonKeys = null) => FrozenRules.Create(
        CompetitionProtocol.CurrentVersion, rulesetId, rulesetVersion, minimumCompatibleVersion,
        modeId, InformationPolicy.SealedAttempt, alternatesFirstAttempt: false, comparisonKeys ?? DefaultKeys);

    [Fact]
    public void Create_rejects_an_empty_ruleset_id()
    {
        Assert.Throws<InvalidFrozenRulesException>(() => CreateDefault(rulesetId: ""));
    }

    [Fact]
    public void Create_rejects_an_empty_mode_id()
    {
        Assert.Throws<InvalidFrozenRulesException>(() => CreateDefault(modeId: ""));
    }

    [Fact]
    public void Create_rejects_a_minimum_compatible_version_above_the_ruleset_version()
    {
        Assert.Throws<InvalidFrozenRulesException>(() => CreateDefault(rulesetVersion: 1, minimumCompatibleVersion: 2));
    }

    [Fact]
    public void Create_rejects_no_comparison_keys()
    {
        Assert.Throws<InvalidFrozenRulesException>(() => CreateDefault(comparisonKeys: []));
    }

    [Fact]
    public void Create_rejects_a_repeated_metric_across_comparison_keys()
    {
        Assert.Throws<InvalidFrozenRulesException>(() => CreateDefault(comparisonKeys:
        [
            new ComparisonKey(ResultMetric.Score, MetricDirection.HigherWins),
            new ComparisonKey(ResultMetric.Score, MetricDirection.LowerWins)
        ]));
    }

    [Fact]
    public void Two_instances_built_from_the_same_values_are_equal()
    {
        var a = CreateDefault();
        var b = CreateDefault();

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Comparison_key_order_is_part_of_equality()
    {
        var a = CreateDefault(comparisonKeys:
        [
            new ComparisonKey(ResultMetric.Score, MetricDirection.HigherWins),
            new ComparisonKey(ResultMetric.CompletionTimeSeconds, MetricDirection.LowerWins)
        ]);
        var b = CreateDefault(comparisonKeys:
        [
            new ComparisonKey(ResultMetric.CompletionTimeSeconds, MetricDirection.LowerWins),
            new ComparisonKey(ResultMetric.Score, MetricDirection.HigherWins)
        ]);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_later_catalog_change_does_not_affect_an_already_frozen_instance()
    {
        // FrozenRules is immutable and has no reference back to any catalog - the invariant
        // "FrozenRules(series at creation) == FrozenRules(series after any later load)" holds
        // simply because nothing on this type re-resolves anything after Create returns.
        var frozen = CreateDefault(rulesetVersion: 1);
        var laterCatalogVersion = CreateDefault(rulesetVersion: 2);

        Assert.Equal(1, frozen.RulesetVersion);
        Assert.NotEqual(frozen, laterCatalogVersion);
    }
}
