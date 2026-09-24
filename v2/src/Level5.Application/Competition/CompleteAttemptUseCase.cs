using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Observability;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

public sealed record CompleteAttemptRequest(PlayerId ActingPlayerId, VersusSeriesId SeriesId, int GameNumber, AttemptId AttemptId, AttemptResult Result);

/// <summary>
/// Idempotent: if this exact attempt was already completed with a semantically identical result
/// (e.g. the client lost the response to an earlier successful call and retried), this returns the
/// originally accepted result instead of re-applying the completion or re-resolving an
/// already-resolved game. A retry carrying a materially different result for the same attempt is
/// rejected as a conflict (<see cref="Level5.Domain.Competition.ConflictingAttemptResultException"/>)
/// rather than silently replacing the accepted result (Competition Protocol V1 section 13).
/// Tolerates a lost optimistic-concurrency race with a single bounded reload-and-reevaluate pass
/// (issue #11): each participant's completion touches only their own attempt, so a racing
/// participant's already-persisted write never invalidates this player's own completion - reloading
/// and reapplying against the fresh state converges instead of failing outright.
/// </summary>
public sealed class CompleteAttemptUseCase(IVersusSeriesStore seriesStore, IClock clock)
{
    private const int MaxAttempts = 3;

    public async Task<SeriesView> ExecuteAsync(CompleteAttemptRequest request, CancellationToken cancellationToken)
    {
        for (var i = 0; i < MaxAttempts; i++)
        {
            var series = await SeriesLookup.LoadForParticipantAsync(seriesStore, request.SeriesId, request.ActingPlayerId, cancellationToken);
            var expectedRevision = series.Revision;

            try
            {
                series.CompleteAttempt(request.ActingPlayerId, request.GameNumber, request.AttemptId, request.Result, clock.UtcNow);
            }
            catch (ConflictingAttemptResultException)
            {
                ApplicationMetrics.AttemptCompleteOutcomes.Increment(ApplicationMetrics.OutcomeTag, "conflicting_result");
                throw;
            }

            // An idempotent replay (this exact attempt was already completed with a semantically
            // identical result) leaves Revision unchanged - no domain mutation occurred, so there is
            // nothing to persist. Saving anyway would issue an unnecessary conditional UPDATE that
            // could spuriously lose to another participant's unrelated concurrent write, turning a
            // pure no-op replay into a false 409, and would re-resolve nothing but still touch the row.
            if (await SeriesLookup.SaveIfChangedAsync(seriesStore, series, expectedRevision, cancellationToken))
            {
                ApplicationMetrics.AttemptCompleteOutcomes.Increment(ApplicationMetrics.OutcomeTag, "success");
                return series.ToView(request.ActingPlayerId);
            }

            // Lost the optimistic-concurrency race (e.g. the other participant's simultaneous
            // completion saved first). Reload the now-current state and reapply - CompleteAttempt's
            // own idempotency/conflict checks then decide the correct outcome against reality
            // rather than the stale snapshot this iteration started from.
        }

        ApplicationMetrics.SeriesConcurrencyConflicts.Increment(ApplicationMetrics.OperationTag, "complete_attempt");
        throw new ConflictException("Series was concurrently modified by another request. Reload and retry.");
    }
}
