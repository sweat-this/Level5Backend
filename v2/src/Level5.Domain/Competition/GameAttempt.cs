using Level5.Domain.Common;
using Level5.Domain.Ids;

namespace Level5.Domain.Competition;

/// <summary>
/// One participant's attempt at a single game within a series. <see cref="Id"/> is the
/// idempotency key: starting an attempt that already exists returns the existing one instead of
/// creating a duplicate, and completing an already-completed attempt is a no-op that returns the
/// original result rather than applying the transition twice.
/// </summary>
public sealed class GameAttempt
{
    public AttemptId Id { get; private set; }
    public PlayerId PlayerId { get; private set; }
    public AttemptStatus Status { get; private set; }
    public Score? Result { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    private GameAttempt(AttemptId id, PlayerId playerId, AttemptStatus status, Score? result, DateTimeOffset startedAt, DateTimeOffset? completedAt)
    {
        Id = id;
        PlayerId = playerId;
        Status = status;
        Result = result;
        StartedAt = startedAt;
        CompletedAt = completedAt;
    }

    public static GameAttempt Start(PlayerId playerId, DateTimeOffset now)
        => new(AttemptId.New(), playerId, AttemptStatus.NotStarted, null, now, null);

    public static GameAttempt Rehydrate(AttemptId id, PlayerId playerId, AttemptStatus status, Score? result, DateTimeOffset startedAt, DateTimeOffset? completedAt)
        => new(id, playerId, status, result, startedAt, completedAt);

    /// <summary>
    /// Records the attempt's result. Idempotent: if this attempt was already completed, the call
    /// succeeds without changing anything, so a client retrying a lost response converges on the
    /// same accepted result instead of erroring or double-applying the completion.
    /// </summary>
    public void Complete(Score result, DateTimeOffset now)
    {
        if (Status == AttemptStatus.Completed)
        {
            return;
        }

        Status = AttemptStatus.Completed;
        Result = result;
        CompletedAt = now;
    }
}
