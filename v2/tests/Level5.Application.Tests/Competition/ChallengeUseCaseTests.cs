using Level5.Application.Common;
using Level5.Application.Competition;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Ids;
using Level5.Domain.Social;
using Xunit;

namespace Level5.Application.Tests.Competition;

public class ChallengeUseCaseTests
{
    private readonly InMemoryVersusSeriesStore _series = new();
    private readonly InMemoryFriendshipStore _friendships = new();
    private readonly FakeClock _clock = new();
    private readonly CreateChallengeUseCase _create;
    private readonly AcceptChallengeUseCase _accept;
    private readonly GetSeriesUseCase _get;

    public ChallengeUseCaseTests()
    {
        _create = new CreateChallengeUseCase(_series, _friendships, _clock);
        _accept = new AcceptChallengeUseCase(_series, _clock);
        _get = new GetSeriesUseCase(_series);
    }

    private async Task MakeFriendsAsync(PlayerId a, PlayerId b)
    {
        var friendship = Friendship.Between(a, b, _clock.UtcNow);
        await _friendships.AddFriendshipAsync(friendship, CancellationToken.None);
    }

    [Fact]
    public async Task Challenging_a_non_friend_is_rejected()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();

        await Assert.ThrowsAsync<FriendshipRequiredException>(() =>
            _create.ExecuteAsync(new CreateChallengeRequest(challenger, opponent, 3), CancellationToken.None));
    }

    [Fact]
    public async Task Challenging_a_friend_creates_a_pending_series()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);

        var view = await _create.ExecuteAsync(new CreateChallengeRequest(challenger, opponent, 3), CancellationToken.None);

        Assert.Equal(Domain.Competition.SeriesStatus.PendingAcceptance, view.Status);
    }

    [Fact]
    public async Task A_non_participant_gets_not_found_instead_of_forbidden()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);
        var view = await _create.ExecuteAsync(new CreateChallengeRequest(challenger, opponent, 3), CancellationToken.None);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _get.ExecuteAsync(new GetSeriesRequest(PlayerId.New(), view.Id), CancellationToken.None));
    }

    [Fact]
    public async Task Getting_an_unknown_series_id_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _get.ExecuteAsync(new GetSeriesRequest(PlayerId.New(), VersusSeriesId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task Accept_by_the_challenger_is_forbidden()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);
        var view = await _create.ExecuteAsync(new CreateChallengeRequest(challenger, opponent, 3), CancellationToken.None);

        await Assert.ThrowsAsync<Domain.Competition.SeriesAuthorizationException>(() =>
            _accept.ExecuteAsync(new AcceptChallengeRequest(challenger, view.Id), CancellationToken.None));
    }
}
