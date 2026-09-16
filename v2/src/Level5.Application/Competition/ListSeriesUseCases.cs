using Level5.Application.Abstractions;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

public sealed record SeriesSummary(
    VersusSeriesId Id, PlayerId ChallengerId, PlayerId OpponentId, SeriesStatus Status,
    int CurrentGameNumber, int TotalGames, long Revision, DateTimeOffset CreatedAt);

public sealed class ListIncomingChallengesUseCase(IVersusSeriesStore seriesStore)
{
    public async Task<IReadOnlyList<SeriesSummary>> ExecuteAsync(PlayerId actingPlayerId, CancellationToken cancellationToken)
    {
        var series = await seriesStore.ListIncomingChallengesAsync(actingPlayerId, cancellationToken);
        return [.. series.Select(ToSummary)];
    }

    internal static SeriesSummary ToSummary(VersusSeries s)
        => new(s.Id, s.ChallengerId, s.OpponentId, s.Status, s.CurrentGameNumber, s.Format.TotalGames, s.Revision, s.CreatedAt);
}

public sealed class ListOutgoingChallengesUseCase(IVersusSeriesStore seriesStore)
{
    public async Task<IReadOnlyList<SeriesSummary>> ExecuteAsync(PlayerId actingPlayerId, CancellationToken cancellationToken)
    {
        var series = await seriesStore.ListOutgoingChallengesAsync(actingPlayerId, cancellationToken);
        return [.. series.Select(ListIncomingChallengesUseCase.ToSummary)];
    }
}

public sealed class ListActiveSeriesUseCase(IVersusSeriesStore seriesStore)
{
    public async Task<IReadOnlyList<SeriesSummary>> ExecuteAsync(PlayerId actingPlayerId, CancellationToken cancellationToken)
    {
        var series = await seriesStore.ListActiveSeriesAsync(actingPlayerId, cancellationToken);
        return [.. series.Select(ListIncomingChallengesUseCase.ToSummary)];
    }
}
