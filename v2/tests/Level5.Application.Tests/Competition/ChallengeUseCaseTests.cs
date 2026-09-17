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
    private readonly FakeRulesetCatalog _catalog = new();
    private readonly FakeClock _clock = new();
    private readonly CreateChallengeUseCase _create;
    private readonly AcceptChallengeUseCase _accept;
    private readonly GetSeriesUseCase _get;

    public ChallengeUseCaseTests()
    {
        _create = new CreateChallengeUseCase(_series, _friendships, _catalog, _clock);
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
            _create.ExecuteAsync(new CreateChallengeRequest(challenger, opponent, "score-only", null, 3), CancellationToken.None));
    }

    [Fact]
    public async Task Challenging_a_friend_creates_a_pending_series()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);

        var view = await _create.ExecuteAsync(new CreateChallengeRequest(challenger, opponent, "score-only", null, 3), CancellationToken.None);

        Assert.Equal(Domain.Competition.SeriesStatus.PendingAcceptance, view.Status);
    }

    [Fact]
    public async Task Challenging_with_an_unknown_ruleset_is_rejected()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);

        await Assert.ThrowsAsync<UnknownRulesetException>(() =>
            _create.ExecuteAsync(new CreateChallengeRequest(challenger, opponent, "no-such-ruleset", null, 3), CancellationToken.None));
    }

    [Fact]
    public async Task Challenging_with_an_unsupported_ruleset_version_is_rejected()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);

        await Assert.ThrowsAsync<RulesetVersionUnsupportedException>(() =>
            _create.ExecuteAsync(new CreateChallengeRequest(challenger, opponent, "score-only", 99, 3), CancellationToken.None));
    }

    [Fact]
    public async Task Created_series_carry_the_catalog_resolved_frozen_rules()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);

        var view = await _create.ExecuteAsync(new CreateChallengeRequest(challenger, opponent, "score-only", null, 3), CancellationToken.None);

        Assert.Equal("score-only", view.Rules.RulesetId);
        Assert.Equal(Domain.Competition.InformationPolicy.SealedAttempt, view.Rules.InformationPolicy);
        Assert.Equal(Domain.Competition.ResultMetric.Score, view.Rules.ComparisonKeys[0].Metric);
    }

    [Fact]
    public async Task A_non_participant_gets_not_found_instead_of_forbidden()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);
        var view = await _create.ExecuteAsync(new CreateChallengeRequest(challenger, opponent, "score-only", null, 3), CancellationToken.None);

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
        var view = await _create.ExecuteAsync(new CreateChallengeRequest(challenger, opponent, "score-only", null, 3), CancellationToken.None);

        await Assert.ThrowsAsync<Domain.Competition.SeriesAuthorizationException>(() =>
            _accept.ExecuteAsync(new AcceptChallengeRequest(challenger, view.Id), CancellationToken.None));
    }
}
