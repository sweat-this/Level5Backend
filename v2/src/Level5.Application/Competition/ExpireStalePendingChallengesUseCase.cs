using Level5.Application.Abstractions;
using Level5.Domain.Competition;

namespace Level5.Application.Competition;

/// <summary>
/// The background expiry sweep's per-tick body (invoked by <c>Level5.Api</c>'s hosted service, but
/// kept here so it stays testable without a real timer). Finds challenges still in
/// <see cref="Domain.Competition.SeriesStatus.PendingAcceptance"/> older than
/// <see cref="IChallengeExpiryPolicy.PendingAcceptanceTimeout"/> and expires each one via the
/// domain's own <see cref="Domain.Competition.VersusSeries.Expire"/> transition, using the same
/// conditional-update optimistic-concurrency save every other series mutation uses. A row that
/// loses the race (some other command - e.g. a concurrent Accept - already moved it out of
/// PendingAcceptance) is simply skipped rather than retried: it is no longer eligible for expiry
/// by definition, and if it is still stale and still pending, the next sweep tick will find it
/// again.
/// </summary>
public sealed class ExpireStalePendingChallengesUseCase(IVersusSeriesStore seriesStore, IClock clock, IChallengeExpiryPolicy expiryPolicy)
{
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var cutoff = now - expiryPolicy.PendingAcceptanceTimeout;

        var staleIds = await seriesStore.FindStalePendingChallengeIdsAsync(cutoff, expiryPolicy.SweepBatchSize, cancellationToken);

        var expiredCount = 0;
        foreach (var id in staleIds)
        {
            var series = await seriesStore.FindByIdAsync(id, cancellationToken);
            if (series is null)
            {
                continue;
            }

            var expectedRevision = series.Revision;
            try
            {
                series.Expire(now);
            }
            catch (IllegalSeriesTransitionException)
            {
                // Some other command (Accept/Decline/Cancel) already moved this series out of
                // PendingAcceptance between the stale-id scan and this load - no longer eligible.
                continue;
            }

            if (series.Revision == expectedRevision)
            {
                // Already expired by an earlier tick that raced this one - nothing to save.
                continue;
            }

            if (await seriesStore.TrySaveAsync(series, expectedRevision, cancellationToken))
            {
                expiredCount++;
            }
        }

        return expiredCount;
    }
}
