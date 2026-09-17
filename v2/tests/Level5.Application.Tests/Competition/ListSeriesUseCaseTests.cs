using Level5.Application.Common;
using Level5.Application.Competition;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Competition;
using Level5.Domain.Ids;
using Level5.Domain.Social;
using Xunit;

namespace Level5.Application.Tests.Competition;

/// <summary>
/// Issue #21: the four correspondence list use cases just forward to
/// <see cref="Level5.Application.Abstractions.IVersusSeriesStore"/>'s summary/paged queries - these
/// tests exercise the filtering/isolation/pagination contract each one must honor. The store
/// projects from relational columns only (proven against real Postgres in
/// <c>VersusSeriesStoreTests</c>); what belongs here is that a summary is never a
/// <see cref="VersusSeries"/> - the type itself carries no rounds/attempts, so a list response can
/// never leak sealed per-game state regardless of how the store produced it.
/// </summary>
public sealed class ListSeriesUseCaseTests
{
    private readonly InMemoryVersusSeriesStore _series = new();
    private readonly InMemoryFriendshipStore _friendships = new();
    private readonly FakeRulesetCatalog _catalog = new();
    private readonly FakeClock _clock = new();
    private readonly CreateChallengeUseCase _create;
    private readonly AcceptChallengeUseCase _accept;

    public ListSeriesUseCaseTests()
    {
        _create = new CreateChallengeUseCase(_series, _friendships, _catalog, _clock);
        _accept = new AcceptChallengeUseCase(_series, _clock);
    }

    private async Task MakeFriendsAsync(PlayerId a, PlayerId b)
        => await _friendships.AddFriendshipAsync(Friendship.Between(a, b, _clock.UtcNow), CancellationToken.None);

    private async Task<VersusSeriesId> ChallengeAsync(PlayerId challenger, PlayerId opponent, int totalGames = 3)
    {
        await MakeFriendsAsync(challenger, opponent);
        var view = await _create.ExecuteAsync(
            new CreateChallengeRequest(challenger, opponent, "score-only", null, totalGames, null, Guid.NewGuid()), CancellationToken.None);
        return view.Id;
    }

    [Fact]
    public async Task Incoming_only_contains_pending_challenges_addressed_to_the_opponent()
    {
        var opponent = PlayerId.New();
        var challenger = PlayerId.New();
        var bystander = PlayerId.New();
        var addressedToOpponent = await ChallengeAsync(challenger, opponent);
        await ChallengeAsync(challenger, bystander);

        var incoming = await new ListIncomingChallengesUseCase(_series)
            .ExecuteAsync(new ListSeriesPageRequest(opponent, null, null), CancellationToken.None);

        var result = Assert.Single(incoming.Items);
        Assert.Equal(addressedToOpponent, result.Id);
    }

    [Fact]
    public async Task Outgoing_only_contains_pending_challenges_sent_by_the_challenger()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        var otherChallenger = PlayerId.New();
        var sent = await ChallengeAsync(challenger, opponent);
        await ChallengeAsync(otherChallenger, opponent);

        var outgoing = await new ListOutgoingChallengesUseCase(_series)
            .ExecuteAsync(new ListSeriesPageRequest(challenger, null, null), CancellationToken.None);

        var result = Assert.Single(outgoing.Items);
        Assert.Equal(sent, result.Id);
    }

    [Fact]
    public async Task Active_only_contains_series_either_participant_has_accepted()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        var stillPending = await ChallengeAsync(challenger, opponent);
        var accepted = await ChallengeAsync(challenger, opponent);
        await _accept.ExecuteAsync(new AcceptChallengeRequest(opponent, accepted), CancellationToken.None);

        var challengerActive = await new ListActiveSeriesUseCase(_series)
            .ExecuteAsync(new ListSeriesPageRequest(challenger, null, null), CancellationToken.None);
        var opponentActive = await new ListActiveSeriesUseCase(_series)
            .ExecuteAsync(new ListSeriesPageRequest(opponent, null, null), CancellationToken.None);

        Assert.Equal(accepted, Assert.Single(challengerActive.Items).Id);
        Assert.Equal(accepted, Assert.Single(opponentActive.Items).Id);
        Assert.DoesNotContain(challengerActive.Items, s => s.Id == stillPending);
    }

    [Fact]
    public async Task An_unrelated_third_player_sees_no_records_in_any_of_the_four_lists()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        var outsider = PlayerId.New();
        var pending = await ChallengeAsync(challenger, opponent);
        var active = await ChallengeAsync(challenger, opponent);
        await _accept.ExecuteAsync(new AcceptChallengeRequest(opponent, active), CancellationToken.None);

        var request = new ListSeriesPageRequest(outsider, null, null);
        var incoming = await new ListIncomingChallengesUseCase(_series).ExecuteAsync(request, CancellationToken.None);
        var outgoing = await new ListOutgoingChallengesUseCase(_series).ExecuteAsync(request, CancellationToken.None);
        var activeList = await new ListActiveSeriesUseCase(_series).ExecuteAsync(request, CancellationToken.None);
        var completed = await new ListCompletedSeriesUseCase(_series).ExecuteAsync(request, CancellationToken.None);

        Assert.Empty(incoming.Items);
        Assert.Empty(outgoing.Items);
        Assert.Empty(activeList.Items);
        Assert.Empty(completed.Items);
        Assert.DoesNotContain(incoming.Items.Concat(outgoing.Items).Concat(activeList.Items).Concat(completed.Items), s => s.Id == pending || s.Id == active);
    }

    [Fact]
    public async Task A_requested_limit_is_honored_and_the_returned_cursor_reaches_the_remaining_rows()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        var first = await ChallengeAsync(challenger, opponent);
        var second = await ChallengeAsync(challenger, opponent);
        var third = await ChallengeAsync(challenger, opponent);

        var useCase = new ListOutgoingChallengesUseCase(_series);
        var firstPage = await useCase.ExecuteAsync(new ListSeriesPageRequest(challenger, 1, null), CancellationToken.None);

        Assert.Single(firstPage.Items);
        Assert.NotNull(firstPage.NextCursor);

        var secondPage = await useCase.ExecuteAsync(new ListSeriesPageRequest(challenger, 1, firstPage.NextCursor), CancellationToken.None);
        Assert.Single(secondPage.Items);
        Assert.NotEqual(firstPage.Items[0].Id, secondPage.Items[0].Id);

        var thirdPage = await useCase.ExecuteAsync(new ListSeriesPageRequest(challenger, 1, secondPage.NextCursor), CancellationToken.None);
        Assert.Single(thirdPage.Items);
        Assert.Null(thirdPage.NextCursor);

        var collectedIds = new[] { firstPage.Items[0].Id, secondPage.Items[0].Id, thirdPage.Items[0].Id };
        Assert.Equal(new[] { first, second, third }.OrderBy(id => id.Value), collectedIds.OrderBy(id => id.Value));
    }

    [Fact]
    public async Task Omitting_a_limit_falls_back_to_the_documented_default()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await ChallengeAsync(challenger, opponent);

        var page = await new ListOutgoingChallengesUseCase(_series)
            .ExecuteAsync(new ListSeriesPageRequest(challenger, null, null), CancellationToken.None);

        // Well under SeriesListPaging.DefaultLimit, so a correct default never truncates this page -
        // this just proves ExecuteAsync doesn't require a caller-supplied limit to work at all.
        Assert.Single(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task A_cursor_issued_by_one_list_query_is_rejected_by_a_different_one()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await ChallengeAsync(challenger, opponent);
        await ChallengeAsync(challenger, opponent);

        var outgoingFirstPage = await new ListOutgoingChallengesUseCase(_series)
            .ExecuteAsync(new ListSeriesPageRequest(challenger, 1, null), CancellationToken.None);
        Assert.NotNull(outgoingFirstPage.NextCursor);

        // A well-formed cursor from /outgoing must not be silently reinterpreted by /active - its
        // sort key means something different there (CreatedAt is shared, but the two queries are
        // not interchangeable in general, e.g. /completed keys on CompletedAt instead).
        await Assert.ThrowsAsync<ValidationFailedException>(() => new ListActiveSeriesUseCase(_series)
            .ExecuteAsync(new ListSeriesPageRequest(challenger, 1, outgoingFirstPage.NextCursor), CancellationToken.None));
    }
}
