using Level5.Application.Common;
using Level5.Application.Competition;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Competition;
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
    private readonly DeclineChallengeUseCase _decline;
    private readonly CancelChallengeUseCase _cancel;
    private readonly GetSeriesUseCase _get;
    private readonly ListCompletedSeriesUseCase _listCompleted;

    public ChallengeUseCaseTests()
    {
        _create = new CreateChallengeUseCase(_series, _friendships, _catalog, _clock);
        _accept = new AcceptChallengeUseCase(_series, _clock);
        _decline = new DeclineChallengeUseCase(_series, _clock);
        _cancel = new CancelChallengeUseCase(_series, _clock);
        _get = new GetSeriesUseCase(_series);
        _listCompleted = new ListCompletedSeriesUseCase(_series);
    }

    private async Task MakeFriendsAsync(PlayerId a, PlayerId b)
    {
        var friendship = Friendship.Between(a, b, _clock.UtcNow);
        await _friendships.AddFriendshipAsync(friendship, CancellationToken.None);
    }

    private static CreateChallengeRequest Request(
        PlayerId challenger, PlayerId opponent, string rulesetId = "score-only", int? rulesetVersion = null,
        int totalGames = 3, string? informationPolicy = null, Guid? clientRequestId = null)
        => new(challenger, opponent, rulesetId, rulesetVersion, totalGames, informationPolicy, clientRequestId ?? Guid.NewGuid());

    [Fact]
    public async Task Challenging_a_non_friend_is_rejected()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();

        await Assert.ThrowsAsync<FriendshipRequiredException>(() =>
            _create.ExecuteAsync(Request(challenger, opponent), CancellationToken.None));
    }

    [Fact]
    public async Task Challenging_a_friend_creates_a_pending_series()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);

        var view = await _create.ExecuteAsync(Request(challenger, opponent), CancellationToken.None);

        Assert.Equal(Domain.Competition.SeriesStatus.PendingAcceptance, view.Status);
    }

    [Fact]
    public async Task Challenging_yourself_is_rejected()
    {
        var challenger = PlayerId.New();
        // A self-challenge can never pass the friendship check (a player is never their own
        // friend), so this asserts the actual guard a self-challenge hits at this layer.
        await Assert.ThrowsAsync<FriendshipRequiredException>(() =>
            _create.ExecuteAsync(Request(challenger, challenger), CancellationToken.None));
    }

    [Fact]
    public async Task Challenging_without_a_clientRequestId_is_rejected()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            _create.ExecuteAsync(new CreateChallengeRequest(challenger, opponent, "score-only", null, 3, null, null), CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(25)]
    public async Task Unsupported_series_formats_are_rejected(int totalGames)
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            _create.ExecuteAsync(Request(challenger, opponent, totalGames: totalGames), CancellationToken.None));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public async Task Supported_series_formats_are_accepted(int totalGames)
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);

        var view = await _create.ExecuteAsync(Request(challenger, opponent, totalGames: totalGames), CancellationToken.None);

        Assert.Equal(totalGames, view.TotalGames);
    }

    [Fact]
    public async Task Challenging_with_an_unknown_ruleset_is_rejected()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);

        await Assert.ThrowsAsync<UnknownRulesetException>(() =>
            _create.ExecuteAsync(Request(challenger, opponent, rulesetId: "no-such-ruleset"), CancellationToken.None));
    }

    [Fact]
    public async Task Challenging_with_an_unsupported_ruleset_version_is_rejected()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);

        await Assert.ThrowsAsync<RulesetVersionUnsupportedException>(() =>
            _create.ExecuteAsync(Request(challenger, opponent, rulesetVersion: 99), CancellationToken.None));
    }

    [Fact]
    public async Task Challenging_with_an_unknown_information_policy_is_rejected()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            _create.ExecuteAsync(Request(challenger, opponent, informationPolicy: "NotARealPolicy"), CancellationToken.None));
    }

    [Fact]
    public async Task Challenging_with_an_information_policy_the_ruleset_does_not_use_is_rejected()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);

        // FakeRulesetCatalog's "score-only" entry is SealedAttempt-only.
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            _create.ExecuteAsync(Request(challenger, opponent, informationPolicy: "OpenTarget"), CancellationToken.None));
    }

    [Fact]
    public async Task Challenging_with_the_rulesets_own_information_policy_succeeds()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);

        var view = await _create.ExecuteAsync(Request(challenger, opponent, informationPolicy: "SealedAttempt"), CancellationToken.None);

        Assert.Equal(Domain.Competition.InformationPolicy.SealedAttempt, view.Rules.InformationPolicy);
    }

    [Fact]
    public async Task Created_series_carry_the_catalog_resolved_frozen_rules()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);

        var view = await _create.ExecuteAsync(Request(challenger, opponent), CancellationToken.None);

        Assert.Equal("score-only", view.Rules.RulesetId);
        Assert.Equal(Domain.Competition.InformationPolicy.SealedAttempt, view.Rules.InformationPolicy);
        Assert.Equal(Domain.Competition.ResultMetric.Score, view.Rules.ComparisonKeys[0].Metric);
    }

    [Fact]
    public async Task Create_retry_with_the_same_key_and_same_request_returns_the_existing_series()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);
        var clientRequestId = Guid.NewGuid();

        var first = await _create.ExecuteAsync(Request(challenger, opponent, clientRequestId: clientRequestId), CancellationToken.None);
        var retried = await _create.ExecuteAsync(Request(challenger, opponent, clientRequestId: clientRequestId), CancellationToken.None);

        Assert.Equal(first.Id, retried.Id);
    }

    [Fact]
    public async Task Create_retry_does_not_create_a_second_series()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);
        var clientRequestId = Guid.NewGuid();

        await _create.ExecuteAsync(Request(challenger, opponent, clientRequestId: clientRequestId), CancellationToken.None);
        await _create.ExecuteAsync(Request(challenger, opponent, clientRequestId: clientRequestId), CancellationToken.None);

        var outgoing = await new ListOutgoingChallengesUseCase(_series).ExecuteAsync(new ListSeriesPageRequest(challenger, null, null), CancellationToken.None);
        Assert.Single(outgoing.Items);
    }

    [Fact]
    public async Task Create_with_the_same_key_but_a_different_request_conflicts()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        var otherOpponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);
        await MakeFriendsAsync(challenger, otherOpponent);
        var clientRequestId = Guid.NewGuid();

        await _create.ExecuteAsync(Request(challenger, opponent, clientRequestId: clientRequestId), CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _create.ExecuteAsync(Request(challenger, otherOpponent, clientRequestId: clientRequestId), CancellationToken.None));
    }

    [Fact]
    public async Task Create_with_the_same_key_but_a_different_format_conflicts()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);
        var clientRequestId = Guid.NewGuid();

        await _create.ExecuteAsync(Request(challenger, opponent, totalGames: 3, clientRequestId: clientRequestId), CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _create.ExecuteAsync(Request(challenger, opponent, totalGames: 5, clientRequestId: clientRequestId), CancellationToken.None));
    }

    [Fact]
    public async Task Different_challengers_may_reuse_the_same_clientRequestId_independently()
    {
        var challengerA = PlayerId.New();
        var challengerB = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challengerA, opponent);
        await MakeFriendsAsync(challengerB, opponent);
        var sharedKey = Guid.NewGuid();

        var viewA = await _create.ExecuteAsync(Request(challengerA, opponent, clientRequestId: sharedKey), CancellationToken.None);
        var viewB = await _create.ExecuteAsync(Request(challengerB, opponent, clientRequestId: sharedKey), CancellationToken.None);

        Assert.NotEqual(viewA.Id, viewB.Id);
    }

    [Fact]
    public async Task A_non_participant_gets_not_found_instead_of_forbidden()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);
        var view = await _create.ExecuteAsync(Request(challenger, opponent), CancellationToken.None);

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
        var view = await _create.ExecuteAsync(Request(challenger, opponent), CancellationToken.None);

        await Assert.ThrowsAsync<Domain.Competition.SeriesAuthorizationException>(() =>
            _accept.ExecuteAsync(new AcceptChallengeRequest(challenger, view.Id), CancellationToken.None));
    }

    [Fact]
    public async Task Accept_by_the_opponent_activates_the_series()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);
        var view = await _create.ExecuteAsync(Request(challenger, opponent), CancellationToken.None);

        var accepted = await _accept.ExecuteAsync(new AcceptChallengeRequest(opponent, view.Id), CancellationToken.None);

        Assert.Equal(Domain.Competition.SeriesStatus.Active, accepted.Status);
    }

    [Fact]
    public async Task Accept_retry_by_the_opponent_returns_the_current_view_instead_of_conflicting()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);
        var view = await _create.ExecuteAsync(Request(challenger, opponent), CancellationToken.None);
        await _accept.ExecuteAsync(new AcceptChallengeRequest(opponent, view.Id), CancellationToken.None);

        var retried = await _accept.ExecuteAsync(new AcceptChallengeRequest(opponent, view.Id), CancellationToken.None);

        Assert.Equal(Domain.Competition.SeriesStatus.Active, retried.Status);
    }

    [Fact]
    public async Task Accept_after_decline_is_a_conflict()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);
        var view = await _create.ExecuteAsync(Request(challenger, opponent), CancellationToken.None);
        await _decline.ExecuteAsync(new DeclineChallengeRequest(opponent, view.Id), CancellationToken.None);

        await Assert.ThrowsAsync<Domain.Competition.IllegalSeriesTransitionException>(() =>
            _accept.ExecuteAsync(new AcceptChallengeRequest(opponent, view.Id), CancellationToken.None));
    }

    [Fact]
    public async Task Opponent_decline_succeeds()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);
        var view = await _create.ExecuteAsync(Request(challenger, opponent), CancellationToken.None);

        var declined = await _decline.ExecuteAsync(new DeclineChallengeRequest(opponent, view.Id), CancellationToken.None);

        Assert.Equal(Domain.Competition.SeriesStatus.Declined, declined.Status);
    }

    [Fact]
    public async Task Challenger_cannot_decline()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);
        var view = await _create.ExecuteAsync(Request(challenger, opponent), CancellationToken.None);

        await Assert.ThrowsAsync<Domain.Competition.SeriesAuthorizationException>(() =>
            _decline.ExecuteAsync(new DeclineChallengeRequest(challenger, view.Id), CancellationToken.None));
    }

    [Fact]
    public async Task Challenger_cancel_succeeds()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);
        var view = await _create.ExecuteAsync(Request(challenger, opponent), CancellationToken.None);

        var cancelled = await _cancel.ExecuteAsync(new CancelChallengeRequest(challenger, view.Id), CancellationToken.None);

        Assert.Equal(Domain.Competition.SeriesStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public async Task Opponent_cannot_cancel()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);
        var view = await _create.ExecuteAsync(Request(challenger, opponent), CancellationToken.None);

        await Assert.ThrowsAsync<Domain.Competition.SeriesAuthorizationException>(() =>
            _cancel.ExecuteAsync(new CancelChallengeRequest(opponent, view.Id), CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_after_accept_is_a_conflict()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);
        var view = await _create.ExecuteAsync(Request(challenger, opponent), CancellationToken.None);
        await _accept.ExecuteAsync(new AcceptChallengeRequest(opponent, view.Id), CancellationToken.None);

        await Assert.ThrowsAsync<Domain.Competition.IllegalSeriesTransitionException>(() =>
            _cancel.ExecuteAsync(new CancelChallengeRequest(challenger, view.Id), CancellationToken.None));
    }

    [Fact]
    public async Task Completed_list_contains_only_series_that_actually_finished_play()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        var bystander = PlayerId.New();
        await MakeFriendsAsync(challenger, opponent);
        await MakeFriendsAsync(challenger, bystander);

        var toComplete = await _create.ExecuteAsync(Request(challenger, opponent, totalGames: 1), CancellationToken.None);
        await _accept.ExecuteAsync(new AcceptChallengeRequest(opponent, toComplete.Id), CancellationToken.None);
        var challengerAttempt = await new StartAttemptUseCase(_series, _clock).ExecuteAsync(new StartAttemptRequest(challenger, toComplete.Id, 1), CancellationToken.None);
        var opponentAttempt = await new StartAttemptUseCase(_series, _clock).ExecuteAsync(new StartAttemptRequest(opponent, toComplete.Id, 1), CancellationToken.None);
        await new CompleteAttemptUseCase(_series, _clock).ExecuteAsync(new CompleteAttemptRequest(challenger, toComplete.Id, 1, challengerAttempt.AttemptId, AttemptResult.OfScore(90)), CancellationToken.None);
        await new CompleteAttemptUseCase(_series, _clock).ExecuteAsync(new CompleteAttemptRequest(opponent, toComplete.Id, 1, opponentAttempt.AttemptId, AttemptResult.OfScore(10)), CancellationToken.None);

        var declined = await _create.ExecuteAsync(Request(challenger, opponent, totalGames: 3), CancellationToken.None);
        await _decline.ExecuteAsync(new DeclineChallengeRequest(opponent, declined.Id), CancellationToken.None);

        var cancelled = await _create.ExecuteAsync(Request(challenger, opponent, totalGames: 5), CancellationToken.None);
        await _cancel.ExecuteAsync(new CancelChallengeRequest(challenger, cancelled.Id), CancellationToken.None);

        var stillPending = await _create.ExecuteAsync(Request(challenger, bystander, totalGames: 7), CancellationToken.None);

        var challengerCompleted = await _listCompleted.ExecuteAsync(new ListSeriesPageRequest(challenger, null, null), CancellationToken.None);
        var opponentCompleted = await _listCompleted.ExecuteAsync(new ListSeriesPageRequest(opponent, null, null), CancellationToken.None);

        var challengerCompletedId = Assert.Single(challengerCompleted.Items).Id;
        Assert.Equal(toComplete.Id, challengerCompletedId);
        var opponentCompletedId = Assert.Single(opponentCompleted.Items).Id;
        Assert.Equal(toComplete.Id, opponentCompletedId);
        Assert.DoesNotContain(challengerCompleted.Items, s => s.Id == declined.Id || s.Id == cancelled.Id || s.Id == stillPending.Id);
    }
}
