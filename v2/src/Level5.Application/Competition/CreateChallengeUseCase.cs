using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Competition;

public sealed record CreateChallengeRequest(PlayerId ChallengerId, PlayerId OpponentId, int TotalGames);

/// <summary>
/// Only accepted friends may challenge each other for the correspondence MVP - broader discovery
/// or matchmaking is explicitly out of scope.
/// </summary>
public sealed class CreateChallengeUseCase(
    IVersusSeriesStore seriesStore,
    IFriendshipStore friendshipStore,
    IClock clock)
{
    public async Task<SeriesView> ExecuteAsync(CreateChallengeRequest request, CancellationToken cancellationToken)
    {
        if (!await friendshipStore.AreFriendsAsync(request.ChallengerId, request.OpponentId, cancellationToken))
        {
            throw new FriendshipRequiredException("You can only challenge an accepted friend.");
        }

        var format = SeriesFormat.BestOf(request.TotalGames);
        var series = VersusSeries.CreateChallenge(request.ChallengerId, request.OpponentId, format, clock.UtcNow);

        await seriesStore.AddAsync(series, cancellationToken);

        return series.ToView(request.ChallengerId);
    }
}
