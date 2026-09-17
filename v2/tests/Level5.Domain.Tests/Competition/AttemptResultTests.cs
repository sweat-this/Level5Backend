using Level5.Domain.Competition;
using Xunit;

namespace Level5.Domain.Tests.Competition;

public class AttemptResultTests
{
    [Fact]
    public void Of_rejects_an_empty_metric_set()
    {
        Assert.Throws<InvalidAttemptResultException>(() => AttemptResult.Of(new Dictionary<ResultMetric, double>()));
    }

    [Fact]
    public void OfScore_rejects_a_negative_score()
    {
        Assert.Throws<InvalidAttemptResultException>(() => AttemptResult.OfScore(-1));
    }

    [Theory]
    [InlineData(ResultMetric.Score)]
    [InlineData(ResultMetric.ShotsMade)]
    [InlineData(ResultMetric.ShotsAttempted)]
    [InlineData(ResultMetric.Accuracy)]
    [InlineData(ResultMetric.CompletionTimeSeconds)]
    [InlineData(ResultMetric.LongestStreak)]
    [InlineData(ResultMetric.TotalDistance)]
    [InlineData(ResultMetric.BonusPoints)]
    public void Of_rejects_a_negative_protocol_metric(ResultMetric metric)
    {
        Assert.Throws<InvalidAttemptResultException>(() => AttemptResult.Of(
            new Dictionary<ResultMetric, double> { [metric] = -1 }));
    }

    [Fact]
    public void Of_rejects_accuracy_above_one_hundred()
    {
        Assert.Throws<InvalidAttemptResultException>(() => AttemptResult.Of(
            new Dictionary<ResultMetric, double> { [ResultMetric.Accuracy] = 100.01 }));
    }

    [Fact]
    public void Of_rejects_more_made_shots_than_attempted_shots()
    {
        Assert.Throws<InvalidAttemptResultException>(() => AttemptResult.Of(new Dictionary<ResultMetric, double>
        {
            [ResultMetric.ShotsMade] = 6,
            [ResultMetric.ShotsAttempted] = 5
        }));
    }

    [Fact]
    public void Of_rejects_an_undefined_metric()
    {
        Assert.Throws<InvalidAttemptResultException>(() => AttemptResult.Of(
            new Dictionary<ResultMetric, double> { [(ResultMetric)999] = 1 }));
    }

    [Fact]
    public void ValueOf_returns_null_for_a_metric_the_result_does_not_carry()
    {
        var result = AttemptResult.OfScore(90);

        Assert.Null(result.ValueOf(ResultMetric.CompletionTimeSeconds));
    }

    [Fact]
    public void Equality_does_not_depend_on_insertion_order()
    {
        var a = AttemptResult.Of(new Dictionary<ResultMetric, double>
        {
            [ResultMetric.Score] = 50,
            [ResultMetric.Accuracy] = 0.8
        });
        var b = AttemptResult.Of(new Dictionary<ResultMetric, double>
        {
            [ResultMetric.Accuracy] = 0.8,
            [ResultMetric.Score] = 50
        });

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_fails_when_a_metric_value_differs()
    {
        var a = AttemptResult.OfScore(50);
        var b = AttemptResult.OfScore(51);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equality_fails_when_the_metric_sets_differ_even_with_overlapping_values()
    {
        var a = AttemptResult.Of(new Dictionary<ResultMetric, double> { [ResultMetric.Score] = 50 });
        var b = AttemptResult.Of(new Dictionary<ResultMetric, double>
        {
            [ResultMetric.Score] = 50,
            [ResultMetric.Accuracy] = 0.5
        });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Every_named_protocol_metric_can_be_carried()
    {
        var allMetrics = Enum.GetValues<ResultMetric>();
        var metrics = allMetrics.ToDictionary(m => m, m => 1.0);

        var result = AttemptResult.Of(metrics);

        foreach (var metric in allMetrics)
        {
            Assert.Equal(1.0, result.ValueOf(metric));
        }
    }
}
