using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Observability;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

/// <summary>
/// Shared load-and-authorize step for every series use case. A player who is not a participant
/// gets the same 404 as a series that does not exist at all, so probing random series ids can't
/// be used to learn which ones are real.
/// </summary>
internal static class SeriesLookup
{
    public static async Task<VersusSeries> LoadForParticipantAsync(
        IVersusSeriesStore store, VersusSeriesId id, PlayerId actingPlayerId, CancellationToken cancellationToken)
    {
        var series = await store.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Series not found.");

        if (!series.IsParticipant(actingPlayerId))
        {
            throw new NotFoundException("Series not found.");
        }

        return series;
    }

    /// <summary>
    /// Shared orchestration for Accept/Decline/Cancel (issue #10). <paramref name="command"/> is
    /// the domain transition; it leaves <see cref="VersusSeries.Revision"/> unchanged when the
    /// same command already won (an authorized replay), so that case returns the current view
    /// without writing. A lost optimistic write gets exactly one authoritative re-evaluation:
    /// reload and run the SAME command again. If it now replays as a no-op, the same command won
    /// the race (e.g. a concurrent duplicate Accept) and this caller converges on it; if it
    /// throws, an incompatible command won and that domain conflict surfaces unchanged. If it
    /// would still mutate, the reload does not explain the lost write, so this surfaces a plain
    /// concurrency conflict rather than persisting a second mutation or looping - the challenge
    /// lifecycle has one pending step, so a single reload is always enough to see what won.
    /// </summary>
    public static async Task<SeriesView> ApplyChallengeTransitionAsync(
        IVersusSeriesStore store, VersusSeriesId id, PlayerId actingPlayerId, string operation,
        Action<VersusSeries> command, CancellationToken cancellationToken)
    {
        var series = await LoadForParticipantAsync(store, id, actingPlayerId, cancellationToken);
        var expectedRevision = series.Revision;

        command(series);

        if (await SaveIfChangedAsync(store, series, expectedRevision, cancellationToken))
        {
            return series.ToView(actingPlayerId);
        }

        var current = await LoadForParticipantAsync(store, id, actingPlayerId, cancellationToken);
        var currentRevision = current.Revision;

        command(current);

        if (current.Revision == currentRevision)
        {
            return current.ToView(actingPlayerId);
        }

        ApplicationMetrics.SeriesConcurrencyConflicts.Increment(ApplicationMetrics.OperationTag, operation);
        throw new ConflictException("Series was concurrently modified by another request. Reload and retry.");
    }

    /// <summary>
    /// Persists <paramref name="series"/> only if the command just applied to it actually mutated
    /// it (<see cref="VersusSeries.Revision"/> advanced past <paramref name="expectedRevision"/>) -
    /// an idempotent no-op replay leaves nothing to save, and saving anyway risks losing to an
    /// unrelated concurrent write and turning a pure no-op into a false conflict. Shared by every
    /// series use case that follows this load/mutate/save-if-changed shape.
    /// </summary>
    public static Task<bool> SaveIfChangedAsync(
        IVersusSeriesStore store, VersusSeries series, long expectedRevision, CancellationToken cancellationToken) =>
        series.Revision == expectedRevision
            ? Task.FromResult(true)
            : store.TrySaveAsync(series, expectedRevision, cancellationToken);
}
