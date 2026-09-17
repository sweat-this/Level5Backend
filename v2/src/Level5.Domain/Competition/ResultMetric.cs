namespace Level5.Domain.Competition;

/// <summary>
/// Stable, named Protocol V1 attempt-result metrics (see
/// v2/docs/competition-protocol/README.md section 10). The metric's <em>name</em>, not its enum
/// ordinal, is the cross-system identity - persistence and serialization must always key on the
/// name (<c>ToString()</c>/<c>Enum.Parse</c>), never on the underlying integer value, matching
/// Unity's own <c>AttemptMetric</c> ordinal being a private storage detail rather than a
/// contract.
/// </summary>
public enum ResultMetric
{
    Score,
    ShotsMade,
    ShotsAttempted,
    Accuracy,
    CompletionTimeSeconds,
    LongestStreak,
    TotalDistance,
    BonusPoints
}
