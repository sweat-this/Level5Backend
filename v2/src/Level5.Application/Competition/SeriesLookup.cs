using Level5.Application.Abstractions;
using Level5.Application.Common;
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

    public static async Task SaveOrThrowAsync(
        IVersusSeriesStore store, VersusSeries series, long expectedRevision, CancellationToken cancellationToken)
    {
        var saved = await store.TrySaveAsync(series, expectedRevision, cancellationToken);
        if (!saved)
        {
            throw new ConflictException("Series was concurrently modified by another request. Reload and retry.");
        }
    }
}
