using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Competition;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Competition;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Domain.Players;
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
///
/// Issue #29: each use case also enriches its page with public participant identity in one batched
/// profile lookup - tests below cover that the enrichment is correct, keeps pagination unchanged,
/// and never runs at all against an empty page.
/// </summary>
public sealed class ListSeriesUseCaseTests
{
    private readonly InMemoryVersusSeriesStore _series = new();
    private readonly InMemoryFriendshipStore _friendships = new();
    private readonly InMemoryPlayerProfileStore _profiles = new();
    private readonly FakeRulesetCatalog _catalog = new();
    private readonly FakeClock _clock = new();
    private readonly CreateChallengeUseCase _create;
    private readonly AcceptChallengeUseCase _accept;
    private readonly DeclineChallengeUseCase _decline;
    private readonly CancelChallengeUseCase _cancel;

    public ListSeriesUseCaseTests()
    {
        _create = new CreateChallengeUseCase(_series, _friendships, _catalog, _clock);
        _accept = new AcceptChallengeUseCase(_series, _clock);
        _decline = new DeclineChallengeUseCase(_series, _clock);
        _cancel = new CancelChallengeUseCase(_series, _clock);
    }

    private async Task MakeFriendsAsync(PlayerId a, PlayerId b)
        => await _friendships.AddFriendshipAsync(Friendship.Between(a, b, _clock.UtcNow), CancellationToken.None);

    private async Task<PlayerId> SeedPlayerAsync(string tag)
    {
        var profile = PlayerProfile.Create(AccountId.New(), tag, PlayerTag.Create($"{tag}#0001"), _clock.UtcNow);
        await _profiles.AddAsync(profile, CancellationToken.None);
        return profile.Id;
    }

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
        var opponent = await SeedPlayerAsync("IncomingOpponent");
        var challenger = await SeedPlayerAsync("IncomingChallenger");
        var bystander = await SeedPlayerAsync("IncomingBystander");
        var addressedToOpponent = await ChallengeAsync(challenger, opponent);
        await ChallengeAsync(challenger, bystander);

        var incoming = await new ListIncomingChallengesUseCase(_series, _profiles)
            .ExecuteAsync(new ListSeriesPageRequest(opponent, null, null), CancellationToken.None);

        var result = Assert.Single(incoming.Items);
        Assert.Equal(addressedToOpponent, result.Id);
        Assert.Equal(challenger, result.Challenger.PlayerId);
        Assert.Equal("IncomingChallenger", result.Challenger.DisplayName);
        Assert.Equal(opponent, result.Opponent.PlayerId);
        Assert.Equal("IncomingOpponent", result.Opponent.DisplayName);
    }

    [Fact]
    public async Task Outgoing_only_contains_pending_challenges_sent_by_the_challenger()
    {
        var challenger = await SeedPlayerAsync("OutgoingChallenger");
        var opponent = await SeedPlayerAsync("OutgoingOpponent");
        var otherChallenger = await SeedPlayerAsync("OtherChallenger");
        var sent = await ChallengeAsync(challenger, opponent);
        await ChallengeAsync(otherChallenger, opponent);

        var outgoing = await new ListOutgoingChallengesUseCase(_series, _profiles)
            .ExecuteAsync(new ListSeriesPageRequest(challenger, null, null), CancellationToken.None);

        var result = Assert.Single(outgoing.Items);
        Assert.Equal(sent, result.Id);
        Assert.Equal(challenger, result.Challenger.PlayerId);
        Assert.Equal(opponent, result.Opponent.PlayerId);
    }

    [Fact]
    public async Task Active_only_contains_series_either_participant_has_accepted()
    {
        var challenger = await SeedPlayerAsync("ActiveChallenger");
        var opponent = await SeedPlayerAsync("ActiveOpponent");
        var stillPending = await ChallengeAsync(challenger, opponent);
        var accepted = await ChallengeAsync(challenger, opponent);
        await _accept.ExecuteAsync(new AcceptChallengeRequest(opponent, accepted), CancellationToken.None);

        var challengerActive = await new ListActiveSeriesUseCase(_series, _profiles)
            .ExecuteAsync(new ListSeriesPageRequest(challenger, null, null), CancellationToken.None);
        var opponentActive = await new ListActiveSeriesUseCase(_series, _profiles)
            .ExecuteAsync(new ListSeriesPageRequest(opponent, null, null), CancellationToken.None);

        Assert.Equal(accepted, Assert.Single(challengerActive.Items).Id);
        Assert.Equal(accepted, Assert.Single(opponentActive.Items).Id);
        Assert.DoesNotContain(challengerActive.Items, s => s.Id == stillPending);
    }

    [Fact]
    public async Task An_unrelated_third_player_sees_no_records_in_any_of_the_four_lists()
    {
        var challenger = await SeedPlayerAsync("OutsiderChallenger");
        var opponent = await SeedPlayerAsync("OutsiderOpponent");
        var outsider = await SeedPlayerAsync("Outsider");
        var pending = await ChallengeAsync(challenger, opponent);
        var active = await ChallengeAsync(challenger, opponent);
        await _accept.ExecuteAsync(new AcceptChallengeRequest(opponent, active), CancellationToken.None);

        var request = new ListSeriesPageRequest(outsider, null, null);
        var incoming = await new ListIncomingChallengesUseCase(_series, _profiles).ExecuteAsync(request, CancellationToken.None);
        var outgoing = await new ListOutgoingChallengesUseCase(_series, _profiles).ExecuteAsync(request, CancellationToken.None);
        var activeList = await new ListActiveSeriesUseCase(_series, _profiles).ExecuteAsync(request, CancellationToken.None);
        var completed = await new ListCompletedSeriesUseCase(_series, _profiles).ExecuteAsync(request, CancellationToken.None);

        Assert.Empty(incoming.Items);
        Assert.Empty(outgoing.Items);
        Assert.Empty(activeList.Items);
        Assert.Empty(completed.Items);
        Assert.DoesNotContain(incoming.Items.Concat(outgoing.Items).Concat(activeList.Items).Concat(completed.Items), s => s.Id == pending || s.Id == active);
    }

    [Fact]
    public async Task A_requested_limit_is_honored_and_the_returned_cursor_reaches_the_remaining_rows()
    {
        var challenger = await SeedPlayerAsync("PageChallenger");
        var opponent = await SeedPlayerAsync("PageOpponent");
        var first = await ChallengeAsync(challenger, opponent);
        var second = await ChallengeAsync(challenger, opponent);
        var third = await ChallengeAsync(challenger, opponent);

        var useCase = new ListOutgoingChallengesUseCase(_series, _profiles);
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

        // Enrichment must never alter pagination - every page's rows still carry both participants.
        Assert.All(new[] { firstPage, secondPage, thirdPage }, page => Assert.All(page.Items, item =>
        {
            Assert.Equal(challenger, item.Challenger.PlayerId);
            Assert.Equal(opponent, item.Opponent.PlayerId);
        }));
    }

    [Fact]
    public async Task Omitting_a_limit_falls_back_to_the_documented_default()
    {
        var challenger = await SeedPlayerAsync("DefLimitChallenger");
        var opponent = await SeedPlayerAsync("DefLimitOpponent");
        await ChallengeAsync(challenger, opponent);

        var page = await new ListOutgoingChallengesUseCase(_series, _profiles)
            .ExecuteAsync(new ListSeriesPageRequest(challenger, null, null), CancellationToken.None);

        // Well under SeriesListPaging.DefaultLimit, so a correct default never truncates this page -
        // this just proves ExecuteAsync doesn't require a caller-supplied limit to work at all.
        Assert.Single(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task A_cursor_issued_by_one_list_query_is_rejected_by_a_different_one()
    {
        var challenger = await SeedPlayerAsync("CursorChallenger");
        var opponent = await SeedPlayerAsync("CursorOpponent");
        await ChallengeAsync(challenger, opponent);
        await ChallengeAsync(challenger, opponent);

        var outgoingFirstPage = await new ListOutgoingChallengesUseCase(_series, _profiles)
            .ExecuteAsync(new ListSeriesPageRequest(challenger, 1, null), CancellationToken.None);
        Assert.NotNull(outgoingFirstPage.NextCursor);

        // A well-formed cursor from /outgoing must not be silently reinterpreted by /active - its
        // sort key means something different there (CreatedAt is shared, but the two queries are
        // not interchangeable in general, e.g. /completed keys on CompletedAt instead).
        await Assert.ThrowsAsync<ValidationFailedException>(() => new ListActiveSeriesUseCase(_series, _profiles)
            .ExecuteAsync(new ListSeriesPageRequest(challenger, 1, outgoingFirstPage.NextCursor), CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_page_never_queries_player_profiles()
    {
        var outsider = await SeedPlayerAsync("EmptyPageOutsider");
        var counting = new CountingPlayerProfileStore(_profiles);

        var page = await new ListOutgoingChallengesUseCase(_series, counting)
            .ExecuteAsync(new ListSeriesPageRequest(outsider, null, null), CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Equal(0, counting.FindByIdsCallCount);
    }

    [Fact]
    public async Task Multiple_rows_on_one_page_resolve_participants_in_a_single_batched_profile_lookup()
    {
        var challenger = await SeedPlayerAsync("BatchChallenger");
        var opponent = await SeedPlayerAsync("BatchOpponent");
        await ChallengeAsync(challenger, opponent);
        await ChallengeAsync(challenger, opponent);
        await ChallengeAsync(challenger, opponent);
        var counting = new CountingPlayerProfileStore(_profiles);

        var page = await new ListOutgoingChallengesUseCase(_series, counting)
            .ExecuteAsync(new ListSeriesPageRequest(challenger, null, null), CancellationToken.None);

        Assert.Equal(3, page.Items.Count);
        Assert.Equal(1, counting.FindByIdsCallCount);
    }

    [Fact]
    public async Task A_row_whose_participant_has_no_registered_profile_is_dropped_rather_than_failing_the_whole_page()
    {
        var challenger = await SeedPlayerAsync("NoProfileChallenger");
        var ghostOpponent = PlayerId.New(); // never registered - simulates an FK-backed inconsistency
        var visibleOpponent = await SeedPlayerAsync("NoProfileVisible");
        await MakeFriendsAsync(challenger, ghostOpponent);
        await MakeFriendsAsync(challenger, visibleOpponent);
        await _create.ExecuteAsync(
            new CreateChallengeRequest(challenger, ghostOpponent, "score-only", null, 3, null, Guid.NewGuid()), CancellationToken.None);
        var visible = await _create.ExecuteAsync(
            new CreateChallengeRequest(challenger, visibleOpponent, "score-only", null, 3, null, Guid.NewGuid()), CancellationToken.None);

        var outgoing = await new ListOutgoingChallengesUseCase(_series, _profiles)
            .ExecuteAsync(new ListSeriesPageRequest(challenger, null, null), CancellationToken.None);

        // The orphaned row is silently omitted; the unaffected row is still returned.
        var only = Assert.Single(outgoing.Items);
        Assert.Equal(visible.Id, only.Id);
        Assert.Equal(visibleOpponent, only.Opponent.PlayerId);
    }

    [Fact]
    public async Task History_contains_every_terminal_status_but_not_active_or_pending()
    {
        var challenger = await SeedPlayerAsync("HistoryChallenger");
        var opponent = await SeedPlayerAsync("HistoryOpponent");
        var startAttempt = new StartAttemptUseCase(_series, _clock);
        var completeAttempt = new CompleteAttemptUseCase(_series, _clock);

        var pending = await ChallengeAsync(challenger, opponent, totalGames: 1);

        var active = await ChallengeAsync(challenger, opponent, totalGames: 3);
        await _accept.ExecuteAsync(new AcceptChallengeRequest(opponent, active), CancellationToken.None);

        var completed = await ChallengeAsync(challenger, opponent, totalGames: 1);
        await _accept.ExecuteAsync(new AcceptChallengeRequest(opponent, completed), CancellationToken.None);
        var challengerAttempt = await startAttempt.ExecuteAsync(new StartAttemptRequest(challenger, completed, GameNumber: 1), CancellationToken.None);
        await completeAttempt.ExecuteAsync(new CompleteAttemptRequest(challenger, completed, 1, challengerAttempt.AttemptId, AttemptResult.OfScore(10)), CancellationToken.None);
        var opponentAttempt = await startAttempt.ExecuteAsync(new StartAttemptRequest(opponent, completed, GameNumber: 1), CancellationToken.None);
        await completeAttempt.ExecuteAsync(new CompleteAttemptRequest(opponent, completed, 1, opponentAttempt.AttemptId, AttemptResult.OfScore(5)), CancellationToken.None);

        var declined = await ChallengeAsync(challenger, opponent, totalGames: 1);
        await _decline.ExecuteAsync(new DeclineChallengeRequest(opponent, declined), CancellationToken.None);
        var cancelled = await ChallengeAsync(challenger, opponent, totalGames: 1);
        await _cancel.ExecuteAsync(new CancelChallengeRequest(challenger, cancelled), CancellationToken.None);

        var expired = await ChallengeAsync(challenger, opponent, totalGames: 1);
        var expiredSeries = await _series.FindByIdAsync(expired, CancellationToken.None);
        expiredSeries!.Expire(_clock.UtcNow);
        await _series.TrySaveAsync(expiredSeries, 0, CancellationToken.None);

        var history = await new ListTerminalHistoryUseCase(_series, _profiles)
            .ExecuteAsync(new ListSeriesPageRequest(challenger, null, null), CancellationToken.None);

        var historyIds = history.Items.Select(i => i.Id).ToHashSet();
        Assert.Contains(completed, historyIds);
        Assert.Contains(declined, historyIds);
        Assert.Contains(cancelled, historyIds);
        Assert.Contains(expired, historyIds);
        Assert.DoesNotContain(pending, historyIds);
        Assert.DoesNotContain(active, historyIds);
        Assert.All(history.Items, item => Assert.NotEqual(SeriesStatus.PendingAcceptance, item.Status));
        Assert.All(history.Items, item => Assert.NotEqual(SeriesStatus.Active, item.Status));
    }

    /// <summary>Wraps a real store to count <see cref="IPlayerProfileStore.FindByIdsAsync"/> calls, proving enrichment batches instead of querying per row.</summary>
    private sealed class CountingPlayerProfileStore(IPlayerProfileStore inner) : IPlayerProfileStore
    {
        public int FindByIdsCallCount { get; private set; }

        public Task<PlayerProfile?> FindByIdAsync(PlayerId id, CancellationToken cancellationToken) => inner.FindByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<PlayerProfile>> FindByIdsAsync(IReadOnlyCollection<PlayerId> ids, CancellationToken cancellationToken)
        {
            FindByIdsCallCount++;
            return inner.FindByIdsAsync(ids, cancellationToken);
        }

        public Task<PlayerProfile?> FindByAccountIdAsync(AccountId accountId, CancellationToken cancellationToken) => inner.FindByAccountIdAsync(accountId, cancellationToken);

        public Task<PlayerProfile?> FindByTagAsync(PlayerTag tag, CancellationToken cancellationToken) => inner.FindByTagAsync(tag, cancellationToken);

        public Task<bool> TagExistsAsync(PlayerTag tag, CancellationToken cancellationToken) => inner.TagExistsAsync(tag, cancellationToken);

        public Task AddAsync(PlayerProfile profile, CancellationToken cancellationToken) => inner.AddAsync(profile, cancellationToken);

        public Task UpdateAsync(PlayerProfile profile, CancellationToken cancellationToken) => inner.UpdateAsync(profile, cancellationToken);
    }
}
