using Level5.Domain.Results;
using Xunit;

namespace Level5.Domain.Tests.Results;

public class MatchResultMetricsTests
{
    [Fact]
    public void Of_rejects_an_empty_metric_set()
    {
        Assert.Throws<InvalidMatchResultException>(() => MatchResultMetrics.Of(new Dictionary<MatchResultMetric, double>()));
    }

    [Fact]
    public void Of_rejects_an_undefined_metric()
    {
        Assert.Throws<InvalidMatchResultException>(() => MatchResultMetrics.Of(
            new Dictionary<MatchResultMetric, double> { [(MatchResultMetric)999] = 1 }));
    }

    [Theory]
    [InlineData(MatchResultMetric.TotalPoints)]
    [InlineData(MatchResultMetric.ShotsMade)]
    [InlineData(MatchResultMetric.TotalDistance)]
    [InlineData(MatchResultMetric.CompletionTimeSeconds)]
    [InlineData(MatchResultMetric.LongestStreak)]
    [InlineData(MatchResultMetric.EnemiesKilled)]
    public void Of_rejects_a_negative_value(MatchResultMetric metric)
    {
        Assert.Throws<InvalidMatchResultException>(() => MatchResultMetrics.Of(
            new Dictionary<MatchResultMetric, double> { [metric] = -1 }));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Of_rejects_a_non_finite_value(double value)
    {
        Assert.Throws<InvalidMatchResultException>(() => MatchResultMetrics.Of(
            new Dictionary<MatchResultMetric, double> { [MatchResultMetric.TotalPoints] = value }));
    }

    [Fact]
    public void ValueOf_returns_null_for_a_metric_the_result_does_not_carry()
    {
        var metrics = MatchResultMetrics.Of(new Dictionary<MatchResultMetric, double> { [MatchResultMetric.TotalPoints] = 90 });

        Assert.Null(metrics.ValueOf(MatchResultMetric.CompletionTimeSeconds));
    }

    [Fact]
    public void Equality_does_not_depend_on_insertion_order()
    {
        var a = MatchResultMetrics.Of(new Dictionary<MatchResultMetric, double>
        {
            [MatchResultMetric.TotalPoints] = 50,
            [MatchResultMetric.ShotsMade] = 8
        });
        var b = MatchResultMetrics.Of(new Dictionary<MatchResultMetric, double>
        {
            [MatchResultMetric.ShotsMade] = 8,
            [MatchResultMetric.TotalPoints] = 50
        });

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_fails_when_a_metric_value_differs()
    {
        var a = MatchResultMetrics.Of(new Dictionary<MatchResultMetric, double> { [MatchResultMetric.TotalPoints] = 50 });
        var b = MatchResultMetrics.Of(new Dictionary<MatchResultMetric, double> { [MatchResultMetric.TotalPoints] = 51 });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equality_fails_when_the_metric_sets_differ_even_with_overlapping_values()
    {
        var a = MatchResultMetrics.Of(new Dictionary<MatchResultMetric, double> { [MatchResultMetric.TotalPoints] = 50 });
        var b = MatchResultMetrics.Of(new Dictionary<MatchResultMetric, double>
        {
            [MatchResultMetric.TotalPoints] = 50,
            [MatchResultMetric.ShotsMade] = 5
        });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Every_named_metric_can_be_carried()
    {
        var allMetrics = Enum.GetValues<MatchResultMetric>();
        var values = allMetrics.ToDictionary(m => m, m => 1.0);

        var metrics = MatchResultMetrics.Of(values);

        foreach (var metric in allMetrics)
        {
            Assert.Equal(1.0, metrics.ValueOf(metric));
        }
    }

    [Fact]
    public void Zero_is_an_accepted_value()
    {
        var metrics = MatchResultMetrics.Of(new Dictionary<MatchResultMetric, double> { [MatchResultMetric.EnemiesKilled] = 0 });

        Assert.Equal(0d, metrics.ValueOf(MatchResultMetric.EnemiesKilled));
    }
}
