using System.Diagnostics.Metrics;

namespace Level5.Application.Observability;

/// <summary>
/// The small set of service-level counters that materially help operate the current system
/// (issue #22) - login failures, refresh-session outcomes, series optimistic-concurrency
/// exhaustion, and challenge/attempt replay-or-conflict outcomes. Deliberately a static
/// <see cref="Meter"/>, not a DI-registered service: OpenTelemetry's <c>MeterProvider</c>
/// subscribes to a meter by name (see Level5.Api's telemetry registration), not through the DI
/// object graph, so use cases can record against these stable instruments exactly where the
/// relevant outcome is already known, with no constructor wiring required.
///
/// Every tag value used with these counters must stay low-cardinality and free of account/player/
/// series/attempt identifiers, emails, tokens, or raw exception text - see the tag vocabularies
/// documented on each counter below.
/// </summary>
public static class ApplicationMetrics
{
    public const string MeterName = "Level5.Application";

    public const string ReasonCategoryTag = "reason_category";
    public const string OutcomeTag = "outcome";
    public const string OperationTag = "operation";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Login attempts rejected before a session was issued. Tag <c>reason_category</c>:
    /// <c>bad_username_format</c>, <c>unknown_account</c>, <c>bad_password</c>,
    /// <c>account_disabled</c>. Distinguishing these internally does not weaken
    /// <see cref="Level5.Application.Identity.LoginUseCase"/>'s client-facing contract, which still
    /// always returns the same generic <c>invalid_credentials</c> response regardless of cause.
    /// </summary>
    public static readonly Counter<long> LoginFailures =
        Meter.CreateCounter<long>("auth.login.failure", description: "Login attempts rejected, by reason category.");

    /// <summary>
    /// Refresh-session attempts, by outcome. Tag <c>outcome</c>: <c>success</c>, <c>unknown</c>,
    /// <c>expired</c>, <c>revoked</c>, <c>account_inactive</c>, <c>replay_conflict</c> (lost the
    /// optimistic-concurrency race - the presented credential had already been rotated or revoked
    /// by another request). As with logins, this internal breakdown does not change the single
    /// generic <c>invalid_refresh_token</c> response every failure mode shares.
    /// </summary>
    public static readonly Counter<long> RefreshOutcomes =
        Meter.CreateCounter<long>("auth.refresh.outcome", description: "Refresh-session attempts, by outcome.");

    /// <summary>
    /// A series mutation exhausted its bounded reload-and-reevaluate retries
    /// (<see cref="Level5.Application.Competition.StartAttemptUseCase"/>/
    /// <see cref="Level5.Application.Competition.CompleteAttemptUseCase"/>, or the single
    /// reconciliation reload of an Accept/Decline/Cancel) under persistent optimistic-concurrency
    /// contention. Tag <c>operation</c>: <c>start_attempt</c>, <c>complete_attempt</c>,
    /// <c>accept_challenge</c>, <c>decline_challenge</c>, <c>cancel_challenge</c>.
    /// </summary>
    public static readonly Counter<long> SeriesConcurrencyConflicts =
        Meter.CreateCounter<long>("series.concurrency.conflict", description: "Optimistic-concurrency retries exhausted on a series mutation, by operation.");

    /// <summary>
    /// Challenge-create requests, by outcome. Tag <c>outcome</c>: <c>created</c> (a new series),
    /// <c>idempotent_replay</c> (a sequential retry found a matching <c>clientRequestId</c> already
    /// persisted), <c>idempotent_replay_after_race</c> (the same, but only resolved after losing a
    /// concurrent insert race on that key - a distinct signal worth alerting on separately, since
    /// it indicates real write contention rather than an ordinary client retry), <c>conflict</c> (a
    /// reused <c>clientRequestId</c> for a materially different request).
    /// </summary>
    public static readonly Counter<long> ChallengeCreateOutcomes =
        Meter.CreateCounter<long>("challenge.create.replay_or_conflict", description: "Challenge-create requests, by outcome.");

    /// <summary>
    /// Attempt-complete requests, by outcome. Tag <c>outcome</c>: <c>success</c>,
    /// <c>conflicting_result</c> (a retry submitted a materially different result than the one
    /// already accepted for this attempt). The idempotent-replay case (an identical retry) is
    /// intentionally not split out here - it returns the same <c>success</c> result as the original
    /// call and is not itself an operational signal worth a separate bucket.
    /// </summary>
    public static readonly Counter<long> AttemptCompleteOutcomes =
        Meter.CreateCounter<long>("attempt.complete.outcome", description: "Attempt-complete requests, by outcome.");

    /// <summary>
    /// A single-tag increment - every counter above is ever recorded with exactly one low-cardinality
    /// tag, so this keeps every call site free of a <see cref="KeyValuePair{TKey,TValue}"/> literal.
    /// </summary>
    public static void Increment(this Counter<long> counter, string tagName, string tagValue) =>
        counter.Add(1, new KeyValuePair<string, object?>(tagName, tagValue));
}
