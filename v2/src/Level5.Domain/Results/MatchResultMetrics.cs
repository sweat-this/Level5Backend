using Level5.Domain.Common;

namespace Level5.Domain.Results;

/// <summary>
/// A completed match's named metric set. At least one metric is required, every key must be a
/// recognized <see cref="MatchResultMetric"/> name, and every value must be a finite,
/// non-negative number - gameplay stats are never negative or fractional-infinite by
/// construction. Equality is semantic (order-independent), mirroring
/// <see cref="Level5.Domain.Competition.AttemptResult"/>.
/// </summary>
public sealed class MatchResultMetrics : IEquatable<MatchResultMetrics>
{
    private readonly Dictionary<MatchResultMetric, double> _metrics;

    private MatchResultMetrics(Dictionary<MatchResultMetric, double> metrics)
    {
        _metrics = metrics;
    }

    public IReadOnlyDictionary<MatchResultMetric, double> Values => _metrics;

    public static MatchResultMetrics Of(IReadOnlyDictionary<MatchResultMetric, double> metrics)
    {
        if (metrics is null || metrics.Count == 0)
        {
            throw new InvalidMatchResultException("A match result must contain at least one metric.");
        }

        foreach (var (metric, value) in metrics)
        {
            if (!Enum.IsDefined(metric))
            {
                throw new InvalidMatchResultException($"Metric '{metric}' is not a recognized match result metric.");
            }

            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new InvalidMatchResultException($"Metric '{metric}' must be a finite number.");
            }

            if (value < 0)
            {
                throw new InvalidMatchResultException($"Metric '{metric}' cannot be negative.");
            }
        }

        return new MatchResultMetrics(new Dictionary<MatchResultMetric, double>(metrics));
    }

    public double? ValueOf(MatchResultMetric metric) => _metrics.TryGetValue(metric, out var value) ? value : null;

    public bool Equals(MatchResultMetrics? other)
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

    public override bool Equals(object? obj) => Equals(obj as MatchResultMetrics);

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
