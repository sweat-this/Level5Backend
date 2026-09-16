namespace Level5.Domain.Ids;

/// <summary>
/// Identifies a single player's attempt at one game within a <see cref="Competition.VersusSeries"/>.
/// Allocated by <c>StartAttempt</c> and used as the idempotency key for <c>CompleteAttempt</c>
/// retries.
/// </summary>
public readonly record struct AttemptId(Guid Value)
{
    public static AttemptId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
