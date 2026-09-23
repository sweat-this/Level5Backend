namespace Level5.Domain.Competition;

/// <summary>One entry in a ruleset's ordered, fallthrough-on-tie comparison sequence.</summary>
public readonly record struct ComparisonKey(ResultMetric Metric, MetricDirection Direction);
