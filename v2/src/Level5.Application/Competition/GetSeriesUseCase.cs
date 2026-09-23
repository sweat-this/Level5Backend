using Level5.Application.Abstractions;
using Level5.Application.Players;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

public sealed record GetSeriesRequest(PlayerId ActingPlayerId, VersusSeriesId SeriesId);

/// <summary>A participant-safe <see cref="SeriesView"/> plus the public identity (issue #29) of both participants.</summary>
public sealed record SeriesDetailView(SeriesView Series, PublicPlayerSummary Challenger, PublicPlayerSummary Opponent);

public sealed class GetSeriesUseCase(IVersusSeriesStore seriesStore, IPlayerProfileStore playerProfileStore)
{
    public async Task<SeriesDetailView> ExecuteAsync(GetSeriesRequest request, CancellationToken cancellationToken)
    {
        var series = await SeriesLookup.LoadForParticipantAsync(seriesStore, request.SeriesId, request.ActingPlayerId, cancellationToken);
        var view = series.ToView(request.ActingPlayerId);

        // Authorization (above) happens before this lookup, so a non-participant can never use a
        // series id to discover participant identity through this path.
        var profiles = await PublicPlayerSummaries.ResolveAsync(
            playerProfileStore, [view.ChallengerId, view.OpponentId], cancellationToken);

        // Unlike a list row, a single series detail has no "drop this row" fallback - a missing
        // participant profile here means the FK-backed data is inconsistent, so this fails the
        // whole response rather than returning a detail view with a silently absent participant.
        if (!profiles.TryGetValue(view.ChallengerId, out var challenger) || !profiles.TryGetValue(view.OpponentId, out var opponent))
        {
            throw new InvalidOperationException(
                $"Series {view.Id.Value} references a participant with no public player profile - FK-backed state is inconsistent.");
        }

        return new SeriesDetailView(view, challenger, opponent);
    }
}
