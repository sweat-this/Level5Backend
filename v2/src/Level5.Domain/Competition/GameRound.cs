using Level5.Domain.Common;
using Level5.Domain.Ids;

namespace Level5.Domain.Competition;

/// <summary>
/// Both participants' attempts at a single game number within a series. A round resolves (and
/// exposes a winner) only once both attempts are completed - this is the mechanism that keeps a
/// sealed result from leaking: the round has no winner and no visible opponent score until then.
/// </summary>
public sealed class GameRound
{
    public int GameNumber { get; private set; }
    public GameAttempt? ChallengerAttempt { get; private set; }
    public GameAttempt? OpponentAttempt { get; private set; }

    private GameRound(int gameNumber, GameAttempt? challengerAttempt, GameAttempt? opponentAttempt)
    {
        GameNumber = gameNumber;
        ChallengerAttempt = challengerAttempt;
        OpponentAttempt = opponentAttempt;
    }

    public static GameRound Empty(int gameNumber) => new(gameNumber, null, null);

    public static GameRound Rehydrate(int gameNumber, GameAttempt? challengerAttempt, GameAttempt? opponentAttempt)
        => new(gameNumber, challengerAttempt, opponentAttempt);

    public bool IsResolved => ChallengerAttempt?.Status == AttemptStatus.Completed
        && OpponentAttempt?.Status == AttemptStatus.Completed;

    /// <summary>The player with the higher score once both attempts are complete; null on a tie or if unresolved.</summary>
    public PlayerId? WinnerId
    {
        get
        {
            if (!IsResolved)
            {
                return null;
            }

            var challengerScore = ScoreOf(ChallengerAttempt!);
            var opponentScore = ScoreOf(OpponentAttempt!);

            if (challengerScore == opponentScore)
            {
                return null;
            }

            return challengerScore > opponentScore ? ChallengerAttempt!.PlayerId : OpponentAttempt!.PlayerId;
        }
    }

    /// <summary>
    /// Round resolution compares only the Score metric, higher-wins - the same hardcoded rule
    /// this type used before <see cref="AttemptResult"/> could carry multiple named metrics. A
    /// series' <see cref="FrozenRules.ComparisonKeys"/>/<see cref="FrozenRules.MinimumCompatibleVersion"/>
    /// are persisted starting with issue #9 but intentionally not consulted here yet - the
    /// ordered, direction-aware comparison engine is issue #11's responsibility.
    /// </summary>
    private static double ScoreOf(GameAttempt attempt) => attempt.Result!.ValueOf(ResultMetric.Score)
        ?? throw new ScoreMetricRequiredException(
            "Round resolution requires a Score metric until issue #11 implements the full ordered comparison engine.");

    public (GameAttempt Attempt, bool WasCreated) StartOrGetAttempt(PlayerId playerId, PlayerId challengerId, DateTimeOffset now)
    {
        var isChallenger = playerId == challengerId;
        var existing = isChallenger ? ChallengerAttempt : OpponentAttempt;
        if (existing is not null)
        {
            return (existing, false);
        }

        var created = GameAttempt.Start(playerId, now);
        if (isChallenger)
        {
            ChallengerAttempt = created;
        }
        else
        {
            OpponentAttempt = created;
        }

        return (created, true);
    }

    public GameAttempt? AttemptFor(PlayerId playerId)
    {
        if (ChallengerAttempt?.PlayerId == playerId)
        {
            return ChallengerAttempt;
        }

        if (OpponentAttempt?.PlayerId == playerId)
        {
            return OpponentAttempt;
        }

        return null;
    }
}

public sealed class ScoreMetricRequiredException : DomainException
{
    public ScoreMetricRequiredException(string message) : base(message)
    {
    }
}
