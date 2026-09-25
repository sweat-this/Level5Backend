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
    /// this same opponent's own prior <see cref="Accept"/>, and <see cref="SeriesStatus.Completed"/>
    /// only from <see cref="SeriesStatus.Active"/> (via <see cref="CompleteAttempt"/>), so either
    /// status proves this exact accept already won. A retried accept (e.g. a lost response) is
    /// then a safe no-op that returns without re-touching the series - <see cref="Revision"/> is
    /// left unchanged, which is how callers detect the replay and skip the write. The challenger
    /// and any non-participant are rejected regardless of status - only the opponent gets this
    /// retry behavior. Declined and Cancelled still fall through to <see cref="EnsureStatus"/>
    /// and conflict.
    /// </summary>
    public void Accept(PlayerId actingPlayerId, DateTimeOffset now) =>
        ApplyTerminalTransition(
            actingPlayerId, OpponentId, "Only the challenged player can accept a challenge.",
            alreadyApplied: status => status is SeriesStatus.Active or SeriesStatus.Completed,
            targetStatus: SeriesStatus.Active, setsCompletedAt: false, now);

    /// <summary>
    /// Idempotent for the opponent: <see cref="SeriesStatus.Declined"/> is only ever reached via
    /// this same opponent's own prior <see cref="Decline"/>, so a retried decline is a no-op that
    /// leaves <see cref="Revision"/> unchanged (see <see cref="Accept"/>). Every other
    /// non-pending status conflicts - it proves a different command won.
    /// </summary>
    public void Decline(PlayerId actingPlayerId, DateTimeOffset now) =>
        ApplyTerminalTransition(
            actingPlayerId, OpponentId, "Only the challenged player can decline a challenge.",
            alreadyApplied: status => status == SeriesStatus.Declined,
            targetStatus: SeriesStatus.Declined, setsCompletedAt: true, now);

    /// <summary>
    /// Idempotent for the challenger: <see cref="SeriesStatus.Cancelled"/> is only ever reached
    /// via this same challenger's own prior <see cref="Cancel"/>, so a retried cancel is a no-op
    /// that leaves <see cref="Revision"/> unchanged (see <see cref="Accept"/>). Every other
    /// non-pending status conflicts - it proves a different command won.
    /// </summary>
    public void Cancel(PlayerId actingPlayerId, DateTimeOffset now) =>
        ApplyTerminalTransition(
            actingPlayerId, ChallengerId, "Only the challenger can cancel a challenge before it is accepted.",
            alreadyApplied: status => status == SeriesStatus.Cancelled,
            targetStatus: SeriesStatus.Cancelled, setsCompletedAt: true, now);

    /// <summary>
    /// Shared shape behind <see cref="Accept"/>/<see cref="Decline"/>/<see cref="Cancel"/>: each is
    /// the one legal transition a specific actor may make out of <see cref="SeriesStatus.PendingAcceptance"/>,
    /// idempotent on its own resulting status, and a conflict against any other status.
    /// </summary>
    private void ApplyTerminalTransition(
        PlayerId actingPlayerId, PlayerId requiredActor, string authorizationErrorMessage,
        Func<SeriesStatus, bool> alreadyApplied, SeriesStatus targetStatus, bool setsCompletedAt, DateTimeOffset now)
    {
        if (actingPlayerId != requiredActor)
        {
            throw new SeriesAuthorizationException(authorizationErrorMessage);
        }

        if (alreadyApplied(Status))
        {
            return;
        }

        EnsureStatus(SeriesStatus.PendingAcceptance);

        Status = targetStatus;
        if (setsCompletedAt)
        {
            CompletedAt = now;
        }

        Touch(now);
    }

    /// <summary>
    /// System-initiated transition: unlike <see cref="Accept"/>/<see cref="Decline"/>/<see cref="Cancel"/>,
    /// this is never invoked by a participant - only the background expiry sweep calls it, once a
    /// challenge has sat in <see cref="SeriesStatus.PendingAcceptance"/> past the configured
    /// timeout. Idempotent: expiring an already-<see cref="SeriesStatus.Expired"/> series is a
    /// safe no-op (<see cref="Revision"/> left unchanged), so a re-run of the sweep against a row
    /// it already expired cannot double-apply. Only legal from <see cref="SeriesStatus.PendingAcceptance"/> -
    /// once a challenge is <see cref="SeriesStatus.Active"/> it can no longer expire.
    /// </summary>
    public void Expire(DateTimeOffset now)
    {
        if (Status == SeriesStatus.Expired)
        {
            return;
        }

        EnsureStatus(SeriesStatus.PendingAcceptance);

        Status = SeriesStatus.Expired;
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
        EnsureIssuanceOrderAllows(actingPlayerId, gameNumber);

        var round = GetOrCreateRound(gameNumber);
        var (attempt, _) = round.StartOrGetAttempt(actingPlayerId, ChallengerId, now);
        Touch(now);
        return attempt;
    }

    /// <summary>
    /// Records a player's result for the given game, binding the submission to the specific
    /// <paramref name="attemptId"/> <see cref="StartAttempt"/> issued so a client can never
    /// complete an attempt it never started. Idempotent for a semantically identical retry
    /// (e.g. a lost response): completing an already-completed attempt with the same accepted
    /// result returns that result without re-applying the transition or re-resolving an
    /// already-resolved round, even if this same completion is what already finished the series.
    /// A retry carrying a materially different result is rejected as a conflict instead of
    /// silently replacing the accepted result (Competition Protocol V1 section 13).
    /// </summary>
    public GameAttempt CompleteAttempt(PlayerId actingPlayerId, int gameNumber, AttemptId attemptId, AttemptResult result, DateTimeOffset now)
    {
        if (!IsParticipant(actingPlayerId))
        {
            throw new SeriesAuthorizationException("Only series participants may act on this series.");
        }

        var existingAttempt = _rounds.FirstOrDefault(r => r.GameNumber == gameNumber)?.AttemptFor(actingPlayerId)
            ?? throw new AttemptNotStartedException("StartAttempt must be called before CompleteAttempt.");

        if (existingAttempt.Id != attemptId)
        {
            throw new AttemptIdentityMismatchException(
                $"Attempt {attemptId} does not match the attempt {existingAttempt.Id} this player started for game {gameNumber}.");
        }

        if (existingAttempt.Status == AttemptStatus.Completed)
        {
            if (Equals(existingAttempt.Result, result))
            {
                return existingAttempt;
            }

            throw new ConflictingAttemptResultException(
                $"Attempt {attemptId} was already completed with a different result. The accepted result cannot be replaced.");
        }

        EnsureStatus(SeriesStatus.Active);
        EnsureCurrentGame(gameNumber);
        EnsureResultSatisfiesFrozenRules(result);

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

    /// <summary>
    /// Every comparison-key metric <see cref="Rules"/> requires must be present in the submitted
    /// result before it is accepted - validated up front so a completed attempt can never end up
    /// unresolvable later, and so a missing metric fails the submission itself (400) rather than
    /// surfacing obscurely at round-resolution time.
    /// </summary>
    private void EnsureResultSatisfiesFrozenRules(AttemptResult result)
    {
        foreach (var key in Rules.ComparisonKeys)
        {
            if (result.ValueOf(key.Metric) is null)
            {
                throw new MissingRequiredMetricException($"Result is missing required metric '{key.Metric}'.");
            }
        }
    }

    /// <summary>
    /// Under <see cref="InformationPolicy.OpenTarget"/>, the non-first-mover may not start their
    /// attempt for this game until the designated first mover has completed theirs (Competition
    /// Protocol V1 section 11, mirroring Unity's <c>VersusGame.CanIssueTo</c>). No gating applies
    /// under <see cref="InformationPolicy.SealedAttempt"/> or to the first mover themselves.
    /// </summary>
    private void EnsureIssuanceOrderAllows(PlayerId actingPlayerId, int gameNumber)
    {
        if (Rules.InformationPolicy != InformationPolicy.OpenTarget)
        {
            return;
        }

        var firstMover = FirstMoverFor(gameNumber);
        if (actingPlayerId == firstMover)
        {
            return;
        }

        var firstMoverAttempt = _rounds.FirstOrDefault(r => r.GameNumber == gameNumber)?.AttemptFor(firstMover);
        if (firstMoverAttempt?.Status != AttemptStatus.Completed)
        {
            throw new OpenTargetIssuanceOrderException(
                $"Game {gameNumber}: the designated first mover must complete their attempt before the other participant may start.");
        }
    }

    /// <summary>
    /// The participant designated to attempt first for a given game under
    /// <see cref="InformationPolicy.OpenTarget"/> (Unity's
    /// <c>SeriesSnapshot.FirstAttemptParticipantIndex</c>): pinned to the challenger unless
    /// <see cref="FrozenRules.AlternatesFirstAttempt"/>, in which case it alternates by game
    /// number.
    /// </summary>
    private PlayerId FirstMoverFor(int gameNumber)
    {
        if (!Rules.AlternatesFirstAttempt)
        {
            return ChallengerId;
        }

        var gameIndex = gameNumber - 1;
        return gameIndex % 2 == 0 ? ChallengerId : OpponentId;
    }

    /// <summary>
    /// The idempotent-replay semantic fingerprint check for a reused challenge-creation
    /// idempotency key: true iff every client-controlled field of a candidate resubmission
    /// matches what this series was originally created with. <paramref name="requestedRulesetVersion"/>
    /// and <paramref name="requestedInformationPolicy"/> are optional on the request - when
    /// omitted, the client is not asserting a value for that field, so it cannot conflict.
    /// Assumes the caller already matched on (ChallengerId, idempotency key) via lookup before
    /// calling this.
    /// </summary>
    public bool MatchesRequest(
        PlayerId opponentId, int totalGames, string rulesetId, int? requestedRulesetVersion,
        InformationPolicy? requestedInformationPolicy) =>
        OpponentId == opponentId
        && Format.TotalGames == totalGames
        && Rules.RulesetId == rulesetId
        && (requestedRulesetVersion is null || Rules.RulesetVersion == requestedRulesetVersion)
        && (requestedInformationPolicy is null || Rules.InformationPolicy == requestedInformationPolicy);

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
            ToAttemptView(yourAttempt, revealResult: true, partialMetric: null),
            ToAttemptView(opponentAttempt, revealResult: round.IsResolved, partialMetric: PartialRevealMetricFor(round, viewerId)));
    }

    /// <summary>
    /// Under <see cref="InformationPolicy.OpenTarget"/>, once the first mover completes but the
    /// round has not yet resolved, the responder may see only the primary comparison metric
    /// (<see cref="FrozenRules.ComparisonKeys"/>'s first entry) of the first mover's result - no
    /// other metric (Competition Protocol V1 section 11, mirroring Unity's
    /// <c>VersusGame.ViewFor</c>). Never applies under <see cref="InformationPolicy.SealedAttempt"/>,
    /// to the first mover's own view of the responder, or once the round is fully resolved (at
    /// which point the ordinary full reveal in <see cref="ToRoundView"/> already applies).
    /// </summary>
    private ResultMetric? PartialRevealMetricFor(GameRound round, PlayerId viewerId)
    {
        if (round.IsResolved || Rules.InformationPolicy != InformationPolicy.OpenTarget)
        {
            return null;
        }

        var firstMover = FirstMoverFor(round.GameNumber);
        if (viewerId == firstMover)
        {
            return null;
        }

        var firstMoverAttempt = round.AttemptFor(firstMover);
        return firstMoverAttempt?.Status == AttemptStatus.Completed ? Rules.ComparisonKeys[0].Metric : null;
    }

    private static AttemptView? ToAttemptView(GameAttempt? attempt, bool revealResult, ResultMetric? partialMetric)
    {
        if (attempt is null)
        {
            return null;
        }

        if (revealResult)
        {
            return new AttemptView(attempt.Id, attempt.Status, attempt.Result?.Metrics);
        }

        if (partialMetric is { } metric && attempt.Result?.ValueOf(metric) is { } value)
        {
            return new AttemptView(attempt.Id, attempt.Status, new Dictionary<ResultMetric, double> { [metric] = value });
        }

        return new AttemptView(attempt.Id, attempt.Status, null);
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
        var challengerWins = _rounds.Count(r => r.IsResolved && r.ResolveWinner(Rules.ComparisonKeys) == ChallengerId);
        var opponentWins = _rounds.Count(r => r.IsResolved && r.ResolveWinner(Rules.ComparisonKeys) == OpponentId);

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

/// <summary>The submitted <see cref="AttemptId"/> does not match the attempt this player actually started for this game - the client can never complete an attempt it did not start.</summary>
public sealed class AttemptIdentityMismatchException : DomainException
{
    public AttemptIdentityMismatchException(string message) : base(message)
    {
    }
}

/// <summary>
/// A retry submitted a materially different result for an attempt that already has an accepted
/// result (Competition Protocol V1 section 13). Maps to HTTP 409 - the accepted result is never
/// silently replaced.
/// </summary>
public sealed class ConflictingAttemptResultException : DomainException
{
    public override string Code => "conflict";

    public ConflictingAttemptResultException(string message) : base(message)
    {
    }
}

/// <summary>
/// Under <see cref="InformationPolicy.OpenTarget"/>, the non-first-mover attempted to start
/// before the designated first mover completed their own attempt for this game.
/// </summary>
public sealed class OpenTargetIssuanceOrderException : DomainException
{
    public override string Code => "conflict";

    public OpenTargetIssuanceOrderException(string message) : base(message)
    {
    }
}
