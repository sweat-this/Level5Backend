using Level5.Domain.Common;

namespace Level5.Domain.Competition;

/// <summary>
/// A completed attempt's full set of named Protocol V1 result metrics (section 10) - replaces
/// the single integer <c>Score</c> the Backend previously carried. Metric identity is explicit
/// (<see cref="ResultMetric"/>, never a positional index) and equality is semantic: two results
/// with the same metric/value pairs are equal regardless of the order they were constructed or
/// deserialized in.
/// </summary>
public sealed class AttemptResult : IEquatable<AttemptResult>
{
    private readonly Dictionary<ResultMetric, double> _metrics;

    private AttemptResult(Dictionary<ResultMetric, double> metrics)
    {
        _metrics = metrics;
    }

    public IReadOnlyDictionary<ResultMetric, double> Metrics => _metrics;

    public static AttemptResult Of(IReadOnlyDictionary<ResultMetric, double> metrics)
    {
        if (metrics is null || metrics.Count == 0)
        {
            throw new InvalidAttemptResultException("An attempt result must contain at least one metric.");
        }

        foreach (var (metric, value) in metrics)
        {
            if (!Enum.IsDefined(metric))
            {
                throw new InvalidAttemptResultException($"Metric '{metric}' is not a recognized Protocol V1 result metric.");
            }

            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new InvalidAttemptResultException($"Metric '{metric}' must be a finite number.");
            }

            if (value < 0)
            {
                throw new InvalidAttemptResultException($"Metric '{metric}' cannot be negative.");
            }

            if (metric == ResultMetric.Accuracy && value > 100)
            {
                throw new InvalidAttemptResultException("Accuracy must be between 0 and 100.");
            }
        }

        if (metrics.TryGetValue(ResultMetric.ShotsMade, out var shotsMade)
            && metrics.TryGetValue(ResultMetric.ShotsAttempted, out var shotsAttempted)
            && shotsMade > shotsAttempted)
        {
            throw new InvalidAttemptResultException("ShotsMade cannot exceed ShotsAttempted.");
        }

        return new AttemptResult(new Dictionary<ResultMetric, double>(metrics));
    }

    /// <summary>
    /// Convenience for the current score-only public API surface. Issue #11 owns exposing full
    /// multi-metric submission over HTTP; until then, every attempt completion the API accepts is
    /// wrapped into this single-metric shape without narrowing the underlying domain/persistence
    /// representation, which already supports every named Protocol V1 metric.
    /// </summary>
    public static AttemptResult OfScore(int score)
    {
        if (score < 0)
        {
            throw new InvalidAttemptResultException("Score cannot be negative.");
        }

        return Of(new Dictionary<ResultMetric, double> { [ResultMetric.Score] = score });
    }

    public double? ValueOf(ResultMetric metric) => _metrics.TryGetValue(metric, out var value) ? value : null;

    public bool Equals(AttemptResult? other)
    {
        if (other is null || _metrics.Count != other._metrics.Count)
        {
            return false;
        }

        foreach (var (metric, value) in _metrics)
        {
            if (!other._metrics.TryGetValue(metric, out var otherValue) || !value.Equals(otherValue))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as AttemptResult);

    public override int GetHashCode()
    {
        // XOR combine so the hash - like Equals - does not depend on insertion/enumeration order.
        var hash = 0;
        foreach (var (metric, value) in _metrics)
        {
            hash ^= HashCode.Combine(metric, value);
        }

        return hash;
    }
}

public sealed class InvalidAttemptResultException : DomainException
{
    public InvalidAttemptResultException(string message) : base(message)
    {
    }
}
