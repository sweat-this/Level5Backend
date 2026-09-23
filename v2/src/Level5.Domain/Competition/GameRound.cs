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

    /// <summary>
    /// The round's winner once both attempts are complete, per the series' frozen, ordered
    /// <paramref name="comparisonKeys"/> (Competition Protocol V1 section 10): each key is
    /// compared in order, skipped on a tie, and the first key that differs decides the winner
    /// (direction-aware - <see cref="MetricDirection.HigherWins"/> or
    /// <see cref="MetricDirection.LowerWins"/>). <c>null</c> if every key ties (a true draw) or
    /// the round is not yet resolved. Mirrors Unity's <c>CompetitiveRuleset.Compare</c>.
    /// </summary>
    public PlayerId? ResolveWinner(IReadOnlyList<ComparisonKey> comparisonKeys)
    {
        if (!IsResolved)
        {
            return null;
        }

        foreach (var key in comparisonKeys)
        {
            var challengerValue = ValueOf(ChallengerAttempt!, key.Metric);
            var opponentValue = ValueOf(OpponentAttempt!, key.Metric);

            if (challengerValue.Equals(opponentValue))
            {
                continue;
            }

            var challengerWins = key.Direction == MetricDirection.HigherWins
                ? challengerValue > opponentValue
                : challengerValue < opponentValue;

            return challengerWins ? ChallengerAttempt!.PlayerId : OpponentAttempt!.PlayerId;
        }

        return null;
    }

    private double ValueOf(GameAttempt attempt, ResultMetric metric) => attempt.Result!.ValueOf(metric)
        ?? throw new MissingRequiredMetricException(
            $"Game {GameNumber}: {attempt.PlayerId}'s accepted result is missing required comparison metric '{metric}'.");

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

/// <summary>
/// A completed attempt's accepted <see cref="AttemptResult"/> does not carry a value for a metric
/// the series' frozen <see cref="FrozenRules.ComparisonKeys"/> requires. Submission-time validation
/// in <see cref="VersusSeries.CompleteAttempt"/> is meant to make this unreachable in practice -
/// this is defense-in-depth at resolution time, not the primary enforcement point.
/// </summary>
public sealed class MissingRequiredMetricException : DomainException
{
    public MissingRequiredMetricException(string message) : base(message)
    {
    }
}
