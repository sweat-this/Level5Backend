using Level5.Application.Abstractions;
using Level5.Domain.Competition;

namespace Level5.Application.Competition;

/// <summary>
/// The background expiry sweep's per-tick body (invoked by <c>Level5.Api</c>'s hosted service, but
/// kept here so it stays testable without a real timer). Finds challenges still in
/// <see cref="Domain.Competition.SeriesStatus.PendingAcceptance"/> older than
/// <see cref="IChallengeExpiryPolicy.PendingAcceptanceTimeout"/> and expires each candidate via a
/// specialized relational compare-and-set. The maintenance path deliberately does not materialize
/// or rewrite nested JSON state; <see cref="Domain.Competition.VersusSeries.Expire"/> remains the
/// canonical in-memory transition. A row that loses the race (some other command - e.g. a
/// concurrent Accept - already moved it out of PendingAcceptance) is skipped without retry.
/// </summary>
public sealed class ExpireStalePendingChallengesUseCase(IVersusSeriesStore seriesStore, IClock clock, IChallengeExpiryPolicy expiryPolicy)
{
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var cutoff = now - expiryPolicy.PendingAcceptanceTimeout;

        var candidates = await seriesStore.FindStalePendingChallengeCandidatesAsync(
            cutoff, expiryPolicy.SweepBatchSize, cancellationToken);

        var expiredCount = 0;
        foreach (var candidate in candidates)
        {
            if (await seriesStore.TryExpireStalePendingChallengeAsync(
                    candidate.Id, candidate.Revision, cutoff, now, cancellationToken))
            {
                expiredCount++;
            }
        }

        return expiredCount;
    }
}
