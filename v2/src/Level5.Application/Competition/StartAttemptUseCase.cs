using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Observability;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

public sealed record StartAttemptRequest(PlayerId ActingPlayerId, VersusSeriesId SeriesId, int GameNumber);

/// <summary>
/// The authoritative, participant-safe descriptor Unity needs to launch a remote attempt through
/// its existing <c>MatchRequest -&gt; MatchConfigurationBuilder -&gt; MatchConfiguration</c> pipeline
/// (Competition Protocol V1 section 14), derived only from the series' persisted, frozen state -
/// never from caller-supplied rules. <see cref="RequiredResultMetrics"/> lists exactly the metric
/// names <see cref="CompleteAttemptRequest.Result"/> must supply, in the same order as
/// <see cref="ComparisonKeys"/>.
/// </summary>
public sealed record AttemptDescriptor(
    VersusSeriesId SeriesId,
    AttemptId AttemptId,
    int GameNumber,
    PlayerId PlayerId,
    int CompetitionProtocolVersion,
    string RulesetId,
    int RulesetVersion,
    int MinimumCompatibleVersion,
    string ModeId,
    InformationPolicy InformationPolicy,
    int TotalGames,
    int GamesToWin,
    IReadOnlyList<ComparisonKey> ComparisonKeys,
    IReadOnlyList<ResultMetric> RequiredResultMetrics);

/// <summary>
/// Idempotent by construction: calling this again for the same player/game before completion
/// returns the same accepted attempt identity and descriptor instead of allocating a new one, so a
/// client that retries after a dropped response does not orphan an attempt. Tolerates a lost
/// optimistic-concurrency race with a single bounded reload-and-reevaluate pass (issue #11) rather
/// than either failing every racer or retrying unboundedly - see
/// <see cref="Level5.Application.Competition.SeriesLookup"/> for the shared load/authorize step.
/// </summary>
public sealed class StartAttemptUseCase(IVersusSeriesStore seriesStore, IClock clock)
{
    private const int MaxAttempts = 3;

    public async Task<AttemptDescriptor> ExecuteAsync(StartAttemptRequest request, CancellationToken cancellationToken)
    {
        for (var i = 0; i < MaxAttempts; i++)
        {
            var series = await SeriesLookup.LoadForParticipantAsync(seriesStore, request.SeriesId, request.ActingPlayerId, cancellationToken);
            var expectedRevision = series.Revision;

            var attempt = series.StartAttempt(request.ActingPlayerId, request.GameNumber, clock.UtcNow);

            // An idempotent replay (this exact attempt already existed) leaves Revision unchanged -
            // no domain mutation occurred, so there is nothing to persist. Saving anyway would issue
            // an unnecessary conditional UPDATE that could spuriously lose to another participant's
            // unrelated concurrent write, turning a pure no-op replay into a false 409.
            if (series.Revision == expectedRevision || await seriesStore.TrySaveAsync(series, expectedRevision, cancellationToken))
            {
                return BuildDescriptor(series, attempt, request.GameNumber);
            }

            // Someone else's write (or a concurrent retry of this same call) won the race. Reload
            // and re-evaluate against current authoritative state rather than blindly retrying the
            // stale mutation - StartAttempt's own idempotency check means this naturally converges
            // whether the winner already created this exact attempt or the state moved on some
            // other way.
        }

        ApplicationMetrics.SeriesConcurrencyConflicts.Increment(ApplicationMetrics.OperationTag, "start_attempt");
        throw new ConflictException("Series was concurrently modified by another request. Reload and retry.");
    }

    private static AttemptDescriptor BuildDescriptor(VersusSeries series, GameAttempt attempt, int gameNumber) => new(
        series.Id,
        attempt.Id,
        gameNumber,
        attempt.PlayerId,
        series.Rules.CompetitionProtocolVersion,
        series.Rules.RulesetId,
        series.Rules.RulesetVersion,
        series.Rules.MinimumCompatibleVersion,
        series.Rules.ModeId,
        series.Rules.InformationPolicy,
        series.Format.TotalGames,
        series.Format.GamesToWin,
        series.Rules.ComparisonKeys,
        [.. series.Rules.ComparisonKeys.Select(k => k.Metric)]);
}
