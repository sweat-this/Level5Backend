using Level5.Domain.Common;
using Level5.Domain.Ids;

namespace Level5.Domain.Competition;

/// <summary>
/// A best-of-N correspondence competition between two players. Server-authoritative: the only
/// way to change this aggregate's state is through its methods, each of which validates the
/// acting player and the current status before applying a transition and bumping
/// <see cref="Revision"/>, the optimistic-concurrency token persistence must check on write.
/// </summary>
public sealed class VersusSeries
{
    private readonly List<GameRound> _rounds = [];

    public VersusSeriesId Id { get; private set; }
    public PlayerId ChallengerId { get; private set; }
    public PlayerId OpponentId { get; private set; }
    public SeriesFormat Format { get; private set; }
    public FrozenRules Rules { get; private set; }
    public SeriesStatus Status { get; private set; }
    public int CurrentGameNumber { get; private set; }
    public PlayerId? WinnerId { get; private set; }
    public long Revision { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public IReadOnlyList<GameRound> Rounds => _rounds;

    private VersusSeries(
        VersusSeriesId id,
        PlayerId challengerId,
        PlayerId opponentId,
        SeriesFormat format,
        FrozenRules rules,
        SeriesStatus status,
        int currentGameNumber,
        PlayerId? winnerId,
        long revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? completedAt)
    {
        Id = id;
        ChallengerId = challengerId;
        OpponentId = opponentId;
        Format = format;
        Rules = rules;
        Status = status;
        CurrentGameNumber = currentGameNumber;
        WinnerId = winnerId;
        Revision = revision;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        CompletedAt = completedAt;
    }

    /// <summary>
    /// Creates a challenge with an already server-resolved, frozen rules snapshot. Callers (the
    /// application layer, via its ruleset catalog port) must resolve <paramref name="rules"/>
    /// from the server's own authoritative source before calling this - this constructor freezes
    /// whatever it is given and never re-resolves it, so a later catalog change cannot
    /// retroactively affect this series (Competition Protocol V1 section 9).
    /// </summary>
    public static VersusSeries CreateChallenge(PlayerId challengerId, PlayerId opponentId, SeriesFormat format, FrozenRules rules, DateTimeOffset now)
    {
        if (challengerId == opponentId)
        {
            throw new InvalidChallengeException("A player cannot challenge themselves.");
        }

        return new VersusSeries(
            VersusSeriesId.New(), challengerId, opponentId, format, rules,
            SeriesStatus.PendingAcceptance, currentGameNumber: 1, winnerId: null,
            revision: 0, createdAt: now, updatedAt: now, completedAt: null);
    }

    public static VersusSeries Rehydrate(
        VersusSeriesId id, PlayerId challengerId, PlayerId opponentId, SeriesFormat format, FrozenRules rules,
        SeriesStatus status, int currentGameNumber, PlayerId? winnerId, long revision,
        DateTimeOffset createdAt, DateTimeOffset updatedAt, DateTimeOffset? completedAt,
        IEnumerable<GameRound> rounds)
    {
        var series = new VersusSeries(id, challengerId, opponentId, format, rules, status, currentGameNumber, winnerId, revision, createdAt, updatedAt, completedAt);
        series._rounds.AddRange(rounds);
        return series;
    }

    public bool IsParticipant(PlayerId playerId) => playerId == ChallengerId || playerId == OpponentId;

    /// <summary>
    /// Idempotent for the opponent: <see cref="SeriesStatus.Active"/> is only ever reached via
    /// this same opponent's own prior <see cref="Accept"/>, so a retried accept call (e.g. a lost
    /// response) is a safe no-op that returns without re-touching the series, rather than an
    /// illegal transition. The challenger and any non-participant are rejected regardless of
    /// status - only the opponent gets this retry behavior. Every other status (Declined,
    /// Cancelled, Completed) still falls through to <see cref="EnsureStatus"/> and conflicts.
    /// </summary>
    public void Accept(PlayerId actingPlayerId, DateTimeOffset now)
    {
        if (actingPlayerId != OpponentId)
        {
            throw new SeriesAuthorizationException("Only the challenged player can accept a challenge.");
        }

        if (Status == SeriesStatus.Active)
        {
            return;
        }

        EnsureStatus(SeriesStatus.PendingAcceptance);

        Status = SeriesStatus.Active;
        Touch(now);
    }

    public void Decline(PlayerId actingPlayerId, DateTimeOffset now)
    {
        if (actingPlayerId != OpponentId)
        {
            throw new SeriesAuthorizationException("Only the challenged player can decline a challenge.");
        }

        EnsureStatus(SeriesStatus.PendingAcceptance);

        Status = SeriesStatus.Declined;
        CompletedAt = now;
        Touch(now);
    }

    public void Cancel(PlayerId actingPlayerId, DateTimeOffset now)
    {
        if (actingPlayerId != ChallengerId)
        {
            throw new SeriesAuthorizationException("Only the challenger can cancel a challenge before it is accepted.");
        }

        EnsureStatus(SeriesStatus.PendingAcceptance);

        Status = SeriesStatus.Cancelled;
        CompletedAt = now;
        Touch(now);
    }

    public GameAttempt StartAttempt(PlayerId actingPlayerId, int gameNumber, DateTimeOffset now)
    {
        if (!IsParticipant(actingPlayerId))
        {
            throw new SeriesAuthorizationException("Only series participants may act on this series.");
        }

        // Idempotency check comes first and is not conditioned on the series still being Active:
        // a client can legitimately retry this call after the series has already moved on (e.g.
        // this very attempt's completion was what finished the series), and that retry must
        // still see its own already-started attempt rather than fail because the series is no
        // longer Active.
        var existingAttempt = _rounds.FirstOrDefault(r => r.GameNumber == gameNumber)?.AttemptFor(actingPlayerId);
        if (existingAttempt is not null)
        {
            return existingAttempt;
        }

        EnsureStatus(SeriesStatus.Active);
        EnsureCurrentGame(gameNumber);

        var round = GetOrCreateRound(gameNumber);
        var (attempt, _) = round.StartOrGetAttempt(actingPlayerId, ChallengerId, now);
        Touch(now);
        return attempt;
    }

    /// <summary>
    /// Records a player's result for the given game. Idempotent - completing an
    /// already-completed attempt again (e.g. a retried request) returns the original result
    /// without applying the transition a second time or re-resolving an already-resolved round,
    /// even if this same completion is what already finished the series.
    /// </summary>
    public GameAttempt CompleteAttempt(PlayerId actingPlayerId, int gameNumber, AttemptResult result, DateTimeOffset now)
    {
        if (!IsParticipant(actingPlayerId))
        {
            throw new SeriesAuthorizationException("Only series participants may act on this series.");
        }

        var existingAttempt = _rounds.FirstOrDefault(r => r.GameNumber == gameNumber)?.AttemptFor(actingPlayerId);
        if (existingAttempt?.Status == AttemptStatus.Completed)
        {
            return existingAttempt;
        }

        EnsureStatus(SeriesStatus.Active);
        EnsureCurrentGame(gameNumber);

        var round = GetOrCreateRound(gameNumber);
        var attempt = round.AttemptFor(actingPlayerId)
            ?? throw new AttemptNotStartedException("StartAttempt must be called before CompleteAttempt.");

        attempt.Complete(result, now);

        if (round.IsResolved)
        {
            AdvanceAfterRoundResolved(round, now);
        }

        Touch(now);
        return attempt;
    }

    public SeriesView ToView(PlayerId viewerId)
    {
        if (!IsParticipant(viewerId))
        {
            throw new SeriesAuthorizationException("Only series participants may view series detail.");
        }

        var rounds = _rounds
            .OrderBy(r => r.GameNumber)
            .Select(r => ToRoundView(r, viewerId))
            .ToList();

        return new SeriesView(
            Id, ChallengerId, OpponentId, Status, Format.TotalGames, Format.GamesToWin,
            CurrentGameNumber, Revision, WinnerId, CreatedAt, CompletedAt, Rules, rounds);
    }

    private GameRoundView ToRoundView(GameRound round, PlayerId viewerId)
    {
        var isViewerChallenger = viewerId == ChallengerId;
        var yourAttempt = isViewerChallenger ? round.ChallengerAttempt : round.OpponentAttempt;
        var opponentAttempt = isViewerChallenger ? round.OpponentAttempt : round.ChallengerAttempt;

        return new GameRoundView(
            round.GameNumber,
            ToAttemptView(yourAttempt, revealResult: true),
            ToAttemptView(opponentAttempt, revealResult: round.IsResolved));
    }

    private static AttemptView? ToAttemptView(GameAttempt? attempt, bool revealResult)
    {
        if (attempt is null)
        {
            return null;
        }

        var result = revealResult ? attempt.Result?.Metrics : null;
        return new AttemptView(attempt.Id, attempt.Status, result);
    }

    private GameRound GetOrCreateRound(int gameNumber)
    {
        var existing = _rounds.FirstOrDefault(r => r.GameNumber == gameNumber);
        if (existing is not null)
        {
            return existing;
        }

        var created = GameRound.Empty(gameNumber);
        _rounds.Add(created);
        return created;
    }

    private void AdvanceAfterRoundResolved(GameRound round, DateTimeOffset now)
    {
        var challengerWins = _rounds.Count(r => r.WinnerId == ChallengerId);
        var opponentWins = _rounds.Count(r => r.WinnerId == OpponentId);

        if (challengerWins >= Format.GamesToWin)
        {
            CompleteWith(ChallengerId, now);
            return;
        }

        if (opponentWins >= Format.GamesToWin)
        {
            CompleteWith(OpponentId, now);
            return;
        }

        if (round.GameNumber == CurrentGameNumber)
        {
            if (CurrentGameNumber >= Format.TotalGames)
            {
                // Every game has been played and neither side reached GamesToWin: the remaining
                // games were exhausted by ties. The series ends without a winner.
                CompleteWith(null, now);
                return;
            }

            CurrentGameNumber++;
        }
    }

    private void CompleteWith(PlayerId? winnerId, DateTimeOffset now)
    {
        Status = SeriesStatus.Completed;
        WinnerId = winnerId;
        CompletedAt = now;
    }

    private void EnsureCurrentGame(int gameNumber)
    {
        if (gameNumber != CurrentGameNumber)
        {
            throw new IllegalSeriesTransitionException(
                $"Game {gameNumber} is not the current game (current game is {CurrentGameNumber}).");
        }
    }

    private void EnsureStatus(SeriesStatus required)
    {
        if (Status != required)
        {
            throw new IllegalSeriesTransitionException($"Series is {Status}, which does not allow this operation.");
        }
    }

    private void Touch(DateTimeOffset now)
    {
        Revision++;
        UpdatedAt = now;
    }
}

public sealed class InvalidChallengeException : DomainException
{
    public InvalidChallengeException(string message) : base(message)
    {
    }
}

public sealed class SeriesAuthorizationException : DomainException
{
    public SeriesAuthorizationException(string message) : base(message)
    {
    }
}

public sealed class IllegalSeriesTransitionException : DomainException
{
    public IllegalSeriesTransitionException(string message) : base(message)
    {
    }
}

public sealed class AttemptNotStartedException : DomainException
{
    public AttemptNotStartedException(string message) : base(message)
    {
    }
}
