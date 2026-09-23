namespace Level5.Domain.Results;

/// <summary>
/// Stable, named general match-result metrics. Persistence and serialization must always key on
/// the metric's <em>name</em> (<c>ToString()</c>/<c>Enum.Parse</c>), never its enum ordinal - this
/// mirrors <see cref="Level5.Domain.Competition.ResultMetric"/>'s own contract, but this set is
/// deliberately separate: an ordinary match result is not a correspondence attempt result, and the
/// two enums must be free to evolve independently.
/// </summary>
public enum MatchResultMetric
{
    TotalPoints,
    ShotsMade,
    TotalDistance,
    CompletionTimeSeconds,
    LongestStreak,
    EnemiesKilled
}
