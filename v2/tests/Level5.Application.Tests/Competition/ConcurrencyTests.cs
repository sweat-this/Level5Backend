using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Competition;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Competition;
using Level5.Domain.Ids;
using Level5.Domain.Social;
using Xunit;

namespace Level5.Application.Tests.Competition;

/// <summary>
/// A store whose write always loses the race, regardless of the revision passed in - stands in
/// for "someone else committed first" without depending on real thread timing to reproduce it.
/// </summary>
internal sealed class AlwaysConflictingVersusSeriesStore(InMemoryVersusSeriesStore inner) : IVersusSeriesStore
{
    public Task<VersusSeries?> FindByIdAsync(VersusSeriesId id, CancellationToken cancellationToken) => inner.FindByIdAsync(id, cancellationToken);
    public Task AddAsync(VersusSeries series, Guid? clientRequestId, CancellationToken cancellationToken) => inner.AddAsync(series, clientRequestId, cancellationToken);
    public Task<VersusSeries?> FindByIdempotencyKeyAsync(PlayerId challengerId, Guid clientRequestId, CancellationToken cancellationToken) => inner.FindByIdempotencyKeyAsync(challengerId, clientRequestId, cancellationToken);
    public Task<PagedResult<SeriesSummary>> ListIncomingChallengeSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken) => inner.ListIncomingChallengeSummariesAsync(playerId, limit, cursor, cancellationToken);
    public Task<PagedResult<SeriesSummary>> ListOutgoingChallengeSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken) => inner.ListOutgoingChallengeSummariesAsync(playerId, limit, cursor, cancellationToken);
    public Task<PagedResult<SeriesSummary>> ListActiveSeriesSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken) => inner.ListActiveSeriesSummariesAsync(playerId, limit, cursor, cancellationToken);
    public Task<PagedResult<SeriesSummary>> ListCompletedSeriesSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken) => inner.ListCompletedSeriesSummariesAsync(playerId, limit, cursor, cancellationToken);
    public Task<bool> TrySaveAsync(VersusSeries series, long expectedRevision, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>
/// Loses exactly its first <paramref name="failCount"/> save attempts (as if another request kept
/// winning the revision race) and then delegates normally - stands in for "the race resolves after
/// one reload" so StartAttempt/CompleteAttempt's bounded reload-and-reevaluate path can be proven
/// without depending on real thread timing.
/// </summary>
internal sealed class FailFirstNSavesVersusSeriesStore(InMemoryVersusSeriesStore inner, int failCount) : IVersusSeriesStore
{
    private int _remainingFailures = failCount;

    public Task<VersusSeries?> FindByIdAsync(VersusSeriesId id, CancellationToken cancellationToken) => inner.FindByIdAsync(id, cancellationToken);
    public Task AddAsync(VersusSeries series, Guid? clientRequestId, CancellationToken cancellationToken) => inner.AddAsync(series, clientRequestId, cancellationToken);
    public Task<VersusSeries?> FindByIdempotencyKeyAsync(PlayerId challengerId, Guid clientRequestId, CancellationToken cancellationToken) => inner.FindByIdempotencyKeyAsync(challengerId, clientRequestId, cancellationToken);
    public Task<PagedResult<SeriesSummary>> ListIncomingChallengeSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken) => inner.ListIncomingChallengeSummariesAsync(playerId, limit, cursor, cancellationToken);
    public Task<PagedResult<SeriesSummary>> ListOutgoingChallengeSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken) => inner.ListOutgoingChallengeSummariesAsync(playerId, limit, cursor, cancellationToken);
    public Task<PagedResult<SeriesSummary>> ListActiveSeriesSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken) => inner.ListActiveSeriesSummariesAsync(playerId, limit, cursor, cancellationToken);
    public Task<PagedResult<SeriesSummary>> ListCompletedSeriesSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken) => inner.ListCompletedSeriesSummariesAsync(playerId, limit, cursor, cancellationToken);

    public Task<bool> TrySaveAsync(VersusSeries series, long expectedRevision, CancellationToken cancellationToken)
    {
        if (_remainingFailures > 0)
        {
            _remainingFailures--;
            return Task.FromResult(false);
        }

        return inner.TrySaveAsync(series, expectedRevision, cancellationToken);
    }
}

/// <summary>
/// Reproduces "another request committed between my read and my write" deterministically: the
/// optional hooks run a competing request against <paramref name="inner"/> immediately before
/// this caller's first write reaches it, after this caller has already loaded (or looked up)
/// stale state. Also counts writes so tests can prove a replay issues none, and that
/// reconciliation is bounded.
/// </summary>
internal sealed class InterceptingVersusSeriesStore(InMemoryVersusSeriesStore inner) : IVersusSeriesStore
{
    public Func<Task>? BeforeFirstSave { get; set; }
    public Func<Task>? BeforeFirstAdd { get; set; }
    public Exception? AddFailure { get; set; }
    public bool LoseEverySave { get; set; }
    public int SaveCalls { get; private set; }

    public Task<VersusSeries?> FindByIdAsync(VersusSeriesId id, CancellationToken cancellationToken) => inner.FindByIdAsync(id, cancellationToken);
    public Task<VersusSeries?> FindByIdempotencyKeyAsync(PlayerId challengerId, Guid clientRequestId, CancellationToken cancellationToken) => inner.FindByIdempotencyKeyAsync(challengerId, clientRequestId, cancellationToken);
    public Task<PagedResult<SeriesSummary>> ListIncomingChallengeSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken) => inner.ListIncomingChallengeSummariesAsync(playerId, limit, cursor, cancellationToken);
    public Task<PagedResult<SeriesSummary>> ListOutgoingChallengeSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken) => inner.ListOutgoingChallengeSummariesAsync(playerId, limit, cursor, cancellationToken);
    public Task<PagedResult<SeriesSummary>> ListActiveSeriesSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken) => inner.ListActiveSeriesSummariesAsync(playerId, limit, cursor, cancellationToken);
    public Task<PagedResult<SeriesSummary>> ListCompletedSeriesSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken) => inner.ListCompletedSeriesSummariesAsync(playerId, limit, cursor, cancellationToken);

    public async Task AddAsync(VersusSeries series, Guid? clientRequestId, CancellationToken cancellationToken)
    {
        if (BeforeFirstAdd is { } competitor)
        {
            BeforeFirstAdd = null;
            await competitor();
        }

        if (AddFailure is { } failure)
        {
            throw failure;
        }

        await inner.AddAsync(series, clientRequestId, cancellationToken);
    }

    public async Task<bool> TrySaveAsync(VersusSeries series, long expectedRevision, CancellationToken cancellationToken)
    {
        SaveCalls++;

        if (BeforeFirstSave is { } competitor)
        {
            BeforeFirstSave = null;
            await competitor();
        }

        return !LoseEverySave && await inner.TrySaveAsync(series, expectedRevision, cancellationToken);
    }
}

/// <summary>
/// Exercises the races the ADR calls out explicitly. The store-level "which write actually wins
/// a stale-revision race" behavior is proven against real Postgres in the Infrastructure
/// integration tests; this layer's own job is narrower and just as important: every mutating use
/// case must resolve a lost race (TrySaveAsync returning false) against authoritative state -
/// converging when the identical command won, and otherwise surfacing a client-visible conflict -
/// instead of silently discarding the loser's request or overwriting the winner.
/// </summary>
public class ConcurrencyTests
{
    private readonly InMemoryVersusSeriesStore _seriesStore = new();
    private readonly InMemoryFriendshipStore _friendships = new();
    private readonly FakeRulesetCatalog _catalog = new();
    private readonly FakeClock _clock = new();

    private async Task<(VersusSeriesId SeriesId, PlayerId Challenger, PlayerId Opponent)> SeedPendingChallengeAsync(int totalGames = 3)
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await _friendships.AddFriendshipAsync(Friendship.Between(challenger, opponent, _clock.UtcNow), CancellationToken.None);

        var view = await new CreateChallengeUseCase(_seriesStore, _friendships, _catalog, _clock)
            .ExecuteAsync(new CreateChallengeRequest(challenger, opponent, "score-only", null, totalGames, null, Guid.NewGuid()), CancellationToken.None);

        return (view.Id, challenger, opponent);
    }

    [Fact]
    public async Task AcceptChallenge_surfaces_a_lost_race_as_a_conflict()
    {
        var (seriesId, _, opponent) = await SeedPendingChallengeAsync();
        var conflicting = new AcceptChallengeUseCase(new AlwaysConflictingVersusSeriesStore(_seriesStore), _clock);

        await Assert.ThrowsAsync<ConflictException>(() =>
            conflicting.ExecuteAsync(new AcceptChallengeRequest(opponent, seriesId), CancellationToken.None));
    }

    [Fact]
    public async Task CancelChallenge_surfaces_a_lost_race_as_a_conflict()
    {
        var (seriesId, challenger, _) = await SeedPendingChallengeAsync();
        var conflicting = new CancelChallengeUseCase(new AlwaysConflictingVersusSeriesStore(_seriesStore), _clock);

        await Assert.ThrowsAsync<ConflictException>(() =>
            conflicting.ExecuteAsync(new CancelChallengeRequest(challenger, seriesId), CancellationToken.None));
    }

    [Fact]
    public async Task CompleteAttempt_surfaces_a_lost_race_as_a_conflict()
    {
        var (seriesId, challenger, opponent) = await SeedPendingChallengeAsync();
        await new AcceptChallengeUseCase(_seriesStore, _clock).ExecuteAsync(new AcceptChallengeRequest(opponent, seriesId), CancellationToken.None);
        var started = await new StartAttemptUseCase(_seriesStore, _clock).ExecuteAsync(new StartAttemptRequest(challenger, seriesId, 1), CancellationToken.None);

        var conflicting = new CompleteAttemptUseCase(new AlwaysConflictingVersusSeriesStore(_seriesStore), _clock);

        await Assert.ThrowsAsync<ConflictException>(() =>
            conflicting.ExecuteAsync(new CompleteAttemptRequest(challenger, seriesId, 1, started.AttemptId, AttemptResult.OfScore(80)), CancellationToken.None));
    }

    [Fact]
    public async Task A_conflicted_write_never_reaches_the_store_as_a_partial_update()
    {
        // Whatever the use case does when TrySaveAsync fails, the underlying data must be
        // untouched - no partial/silent write despite the conflict.
        var (seriesId, _, opponent) = await SeedPendingChallengeAsync();
        var conflicting = new AcceptChallengeUseCase(new AlwaysConflictingVersusSeriesStore(_seriesStore), _clock);

        await Assert.ThrowsAsync<ConflictException>(() =>
            conflicting.ExecuteAsync(new AcceptChallengeRequest(opponent, seriesId), CancellationToken.None));

        var stillPending = await _seriesStore.FindByIdAsync(seriesId, CancellationToken.None);
        Assert.Equal(SeriesStatus.PendingAcceptance, stillPending!.Status);
    }

    [Fact]
    public async Task StartAttempt_reloads_and_returns_a_stable_descriptor_after_a_single_lost_race()
    {
        var (seriesId, challenger, opponent) = await SeedPendingChallengeAsync();
        await new AcceptChallengeUseCase(_seriesStore, _clock).ExecuteAsync(new AcceptChallengeRequest(opponent, seriesId), CancellationToken.None);

        var reevaluating = new StartAttemptUseCase(new FailFirstNSavesVersusSeriesStore(_seriesStore, failCount: 1), _clock);

        var descriptor = await reevaluating.ExecuteAsync(new StartAttemptRequest(challenger, seriesId, 1), CancellationToken.None);

        Assert.Equal(1, descriptor.GameNumber);
        var persisted = await _seriesStore.FindByIdAsync(seriesId, CancellationToken.None);
        Assert.Equal(descriptor.AttemptId, persisted!.Rounds.Single(r => r.GameNumber == 1).AttemptFor(challenger)!.Id);
    }

    [Fact]
    public async Task CompleteAttempt_reloads_and_applies_the_completion_after_a_single_lost_race()
    {
        var (seriesId, challenger, opponent) = await SeedPendingChallengeAsync();
        await new AcceptChallengeUseCase(_seriesStore, _clock).ExecuteAsync(new AcceptChallengeRequest(opponent, seriesId), CancellationToken.None);
        var started = await new StartAttemptUseCase(_seriesStore, _clock).ExecuteAsync(new StartAttemptRequest(challenger, seriesId, 1), CancellationToken.None);

        var reevaluating = new CompleteAttemptUseCase(new FailFirstNSavesVersusSeriesStore(_seriesStore, failCount: 1), _clock);

        var view = await reevaluating.ExecuteAsync(
            new CompleteAttemptRequest(challenger, seriesId, 1, started.AttemptId, AttemptResult.OfScore(80)), CancellationToken.None);

        var round = view.Games.Single(g => g.GameNumber == 1);
        Assert.Equal(80d, round.YourAttempt!.Result![ResultMetric.Score]);
    }

    [Fact]
    public async Task CompleteAttempt_with_a_conflicting_retry_payload_returns_conflict_not_last_write_wins()
    {
        var (seriesId, challenger, opponent) = await SeedPendingChallengeAsync();
        await new AcceptChallengeUseCase(_seriesStore, _clock).ExecuteAsync(new AcceptChallengeRequest(opponent, seriesId), CancellationToken.None);
        var started = await new StartAttemptUseCase(_seriesStore, _clock).ExecuteAsync(new StartAttemptRequest(challenger, seriesId, 1), CancellationToken.None);
        var completeAttempt = new CompleteAttemptUseCase(_seriesStore, _clock);
        await completeAttempt.ExecuteAsync(new CompleteAttemptRequest(challenger, seriesId, 1, started.AttemptId, AttemptResult.OfScore(50)), CancellationToken.None);

        await Assert.ThrowsAsync<Domain.Competition.ConflictingAttemptResultException>(() =>
            completeAttempt.ExecuteAsync(new CompleteAttemptRequest(challenger, seriesId, 1, started.AttemptId, AttemptResult.OfScore(999)), CancellationToken.None));

        var persisted = await _seriesStore.FindByIdAsync(seriesId, CancellationToken.None);
        var attempt = persisted!.Rounds.Single(r => r.GameNumber == 1).AttemptFor(challenger);
        Assert.Equal(50d, attempt!.Result!.ValueOf(ResultMetric.Score));
    }

    /// <summary>Runs one challenge transition as its only authorized actor (opponent for accept/decline, challenger for cancel).</summary>
    private Task<SeriesView> RunTransition(string command, IVersusSeriesStore store, VersusSeriesId seriesId, PlayerId challenger, PlayerId opponent) => command switch
    {
        "accept" => new AcceptChallengeUseCase(store, _clock).ExecuteAsync(new AcceptChallengeRequest(opponent, seriesId), CancellationToken.None),
        "decline" => new DeclineChallengeUseCase(store, _clock).ExecuteAsync(new DeclineChallengeRequest(opponent, seriesId), CancellationToken.None),
        "cancel" => new CancelChallengeUseCase(store, _clock).ExecuteAsync(new CancelChallengeRequest(challenger, seriesId), CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    private static SeriesStatus StatusAfter(string command) => command switch
    {
        "accept" => SeriesStatus.Active,
        "decline" => SeriesStatus.Declined,
        "cancel" => SeriesStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    [InlineData("cancel")]
    public async Task An_already_applied_transition_replays_without_writing(string command)
    {
        var (seriesId, challenger, opponent) = await SeedPendingChallengeAsync();
        await RunTransition(command, _seriesStore, seriesId, challenger, opponent);
        var store = new InterceptingVersusSeriesStore(_seriesStore);

        var replayed = await RunTransition(command, store, seriesId, challenger, opponent);

        Assert.Equal(StatusAfter(command), replayed.Status);
        Assert.Equal(1, replayed.Revision);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task StartAttempt_replay_returns_the_same_descriptor_without_writing()
    {
        var (seriesId, challenger, opponent) = await SeedPendingChallengeAsync();
        await new AcceptChallengeUseCase(_seriesStore, _clock).ExecuteAsync(new AcceptChallengeRequest(opponent, seriesId), CancellationToken.None);
        var original = await new StartAttemptUseCase(_seriesStore, _clock).ExecuteAsync(new StartAttemptRequest(challenger, seriesId, 1), CancellationToken.None);

        var store = new InterceptingVersusSeriesStore(_seriesStore);
        var replayed = await new StartAttemptUseCase(store, _clock).ExecuteAsync(new StartAttemptRequest(challenger, seriesId, 1), CancellationToken.None);

        Assert.Equal(original.AttemptId, replayed.AttemptId);
        Assert.Equal(original.GameNumber, replayed.GameNumber);
        Assert.Equal(original.ComparisonKeys, replayed.ComparisonKeys);
        Assert.Equal(0, store.SaveCalls);
        var persisted = await _seriesStore.FindByIdAsync(seriesId, CancellationToken.None);
        Assert.Equal(2, persisted!.Revision); // unchanged by the replay: 1 for Accept, 1 for the original StartAttempt
    }

    [Fact]
    public async Task StartAttempt_replay_still_succeeds_with_zero_writes_after_the_series_advanced_concurrently()
    {
        // A pure no-op replay must not attempt a conditional UPDATE at all: if it did, its
        // expectedRevision would be captured fresh at load time regardless, but the point of
        // skipping the write entirely is that a replay can never be made to race - there is
        // nothing for a concurrent unrelated write (the other participant's own StartAttempt) to
        // invalidate.
        var (seriesId, challenger, opponent) = await SeedPendingChallengeAsync();
        await new AcceptChallengeUseCase(_seriesStore, _clock).ExecuteAsync(new AcceptChallengeRequest(opponent, seriesId), CancellationToken.None);
        var original = await new StartAttemptUseCase(_seriesStore, _clock).ExecuteAsync(new StartAttemptRequest(challenger, seriesId, 1), CancellationToken.None);
        await new StartAttemptUseCase(_seriesStore, _clock).ExecuteAsync(new StartAttemptRequest(opponent, seriesId, 1), CancellationToken.None);

        var store = new InterceptingVersusSeriesStore(_seriesStore);
        var replayed = await new StartAttemptUseCase(store, _clock).ExecuteAsync(new StartAttemptRequest(challenger, seriesId, 1), CancellationToken.None);

        Assert.Equal(original.AttemptId, replayed.AttemptId);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task CompleteAttempt_identical_replay_returns_stable_state_without_writing()
    {
        var (seriesId, challenger, opponent) = await SeedPendingChallengeAsync();
        await new AcceptChallengeUseCase(_seriesStore, _clock).ExecuteAsync(new AcceptChallengeRequest(opponent, seriesId), CancellationToken.None);
        var started = await new StartAttemptUseCase(_seriesStore, _clock).ExecuteAsync(new StartAttemptRequest(challenger, seriesId, 1), CancellationToken.None);
        var original = await new CompleteAttemptUseCase(_seriesStore, _clock)
            .ExecuteAsync(new CompleteAttemptRequest(challenger, seriesId, 1, started.AttemptId, AttemptResult.OfScore(80)), CancellationToken.None);

        var store = new InterceptingVersusSeriesStore(_seriesStore);
        var replayed = await new CompleteAttemptUseCase(store, _clock)
            .ExecuteAsync(new CompleteAttemptRequest(challenger, seriesId, 1, started.AttemptId, AttemptResult.OfScore(80)), CancellationToken.None);

        Assert.Equal(original.Revision, replayed.Revision);
        Assert.Equal(80d, replayed.Games.Single(g => g.GameNumber == 1).YourAttempt!.Result![ResultMetric.Score]);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task Accept_replay_after_the_series_completed_returns_the_completed_view_without_writing()
    {
        var (seriesId, challenger, opponent) = await SeedPendingChallengeAsync(totalGames: 1);
        await new AcceptChallengeUseCase(_seriesStore, _clock).ExecuteAsync(new AcceptChallengeRequest(opponent, seriesId), CancellationToken.None);
        foreach (var (player, score) in new[] { (challenger, 90), (opponent, 10) })
        {
            var started = await new StartAttemptUseCase(_seriesStore, _clock).ExecuteAsync(new StartAttemptRequest(player, seriesId, 1), CancellationToken.None);
            await new CompleteAttemptUseCase(_seriesStore, _clock).ExecuteAsync(new CompleteAttemptRequest(player, seriesId, 1, started.AttemptId, AttemptResult.OfScore(score)), CancellationToken.None);
        }

        var store = new InterceptingVersusSeriesStore(_seriesStore);
        var replayed = await new AcceptChallengeUseCase(store, _clock).ExecuteAsync(new AcceptChallengeRequest(opponent, seriesId), CancellationToken.None);

        Assert.Equal(SeriesStatus.Completed, replayed.Status);
        Assert.Equal(challenger, replayed.WinnerId);
        Assert.Equal(0, store.SaveCalls);
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    [InlineData("cancel")]
    public async Task A_duplicate_transition_that_loses_the_save_race_converges_on_the_identical_winner(string command)
    {
        var (seriesId, challenger, opponent) = await SeedPendingChallengeAsync();
        // Both requests load revision 0; the duplicate commits first, so this request's save loses.
        var store = new InterceptingVersusSeriesStore(_seriesStore)
        {
            BeforeFirstSave = () => RunTransition(command, _seriesStore, seriesId, challenger, opponent)
        };

        var view = await RunTransition(command, store, seriesId, challenger, opponent);

        Assert.Equal(StatusAfter(command), view.Status);
        Assert.Equal(1, store.SaveCalls);
        var persisted = await _seriesStore.FindByIdAsync(seriesId, CancellationToken.None);
        Assert.Equal(StatusAfter(command), persisted!.Status);
        Assert.Equal(1, persisted.Revision); // exactly one committed transition, not two
    }

    [Theory]
    [InlineData("cancel", "accept")]
    [InlineData("accept", "cancel")]
    [InlineData("decline", "accept")]
    [InlineData("accept", "decline")]
    [InlineData("cancel", "decline")]
    [InlineData("decline", "cancel")]
    public async Task An_incompatible_transition_that_loses_the_save_race_is_a_conflict_and_never_overwrites_the_winner(string winner, string loser)
    {
        var (seriesId, challenger, opponent) = await SeedPendingChallengeAsync();
        var store = new InterceptingVersusSeriesStore(_seriesStore)
        {
            BeforeFirstSave = () => RunTransition(winner, _seriesStore, seriesId, challenger, opponent)
        };

        await Assert.ThrowsAsync<IllegalSeriesTransitionException>(() => RunTransition(loser, store, seriesId, challenger, opponent));

        var persisted = await _seriesStore.FindByIdAsync(seriesId, CancellationToken.None);
        Assert.Equal(StatusAfter(winner), persisted!.Status);
        Assert.Equal(1, persisted.Revision);
    }

    [Fact]
    public async Task Transition_reconciliation_reloads_once_and_never_retries_the_write()
    {
        // Every save loses but the reload still shows PendingAcceptance - nothing explains the
        // lost write, so the use case must surface a conflict instead of writing again or looping.
        var (seriesId, challenger, opponent) = await SeedPendingChallengeAsync();
        var store = new InterceptingVersusSeriesStore(_seriesStore) { LoseEverySave = true };

        await Assert.ThrowsAsync<ConflictException>(() => RunTransition("accept", store, seriesId, challenger, opponent));

        Assert.Equal(1, store.SaveCalls);
        var persisted = await _seriesStore.FindByIdAsync(seriesId, CancellationToken.None);
        Assert.Equal(SeriesStatus.PendingAcceptance, persisted!.Status);
    }

    [Fact]
    public async Task A_create_that_loses_the_insert_race_to_an_identical_request_returns_the_winners_series()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await _friendships.AddFriendshipAsync(Friendship.Between(challenger, opponent, _clock.UtcNow), CancellationToken.None);
        var request = new CreateChallengeRequest(challenger, opponent, "score-only", null, 3, null, Guid.NewGuid());
        SeriesView? winner = null;
        // Commits the identical request after this caller's idempotency lookup missed but before its insert.
        var store = new InterceptingVersusSeriesStore(_seriesStore)
        {
            BeforeFirstAdd = async () => winner = await new CreateChallengeUseCase(_seriesStore, _friendships, _catalog, _clock).ExecuteAsync(request, CancellationToken.None)
        };

        var view = await new CreateChallengeUseCase(store, _friendships, _catalog, _clock).ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(winner!.Id, view.Id);
        var outgoing = await _seriesStore.ListOutgoingChallengeSummariesAsync(challenger, null, null, CancellationToken.None);
        Assert.Single(outgoing.Items);
    }

    [Fact]
    public async Task A_create_that_loses_the_insert_race_to_a_different_request_under_the_same_key_conflicts()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        var otherOpponent = PlayerId.New();
        await _friendships.AddFriendshipAsync(Friendship.Between(challenger, opponent, _clock.UtcNow), CancellationToken.None);
        await _friendships.AddFriendshipAsync(Friendship.Between(challenger, otherOpponent, _clock.UtcNow), CancellationToken.None);
        var key = Guid.NewGuid();
        var store = new InterceptingVersusSeriesStore(_seriesStore)
        {
            BeforeFirstAdd = () => new CreateChallengeUseCase(_seriesStore, _friendships, _catalog, _clock)
                .ExecuteAsync(new CreateChallengeRequest(challenger, otherOpponent, "score-only", null, 3, null, key), CancellationToken.None)
        };

        await Assert.ThrowsAsync<ConflictException>(() => new CreateChallengeUseCase(store, _friendships, _catalog, _clock)
            .ExecuteAsync(new CreateChallengeRequest(challenger, opponent, "score-only", null, 3, null, key), CancellationToken.None));

        var stored = await _seriesStore.FindByIdempotencyKeyAsync(challenger, key, CancellationToken.None);
        Assert.Equal(otherOpponent, stored!.OpponentId);
    }

    [Fact]
    public async Task A_create_insert_conflict_with_no_row_under_the_key_is_not_swallowed_as_a_replay()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await _friendships.AddFriendshipAsync(Friendship.Between(challenger, opponent, _clock.UtcNow), CancellationToken.None);
        var unrelated = new ConflictException("Some other unique constraint.");
        var store = new InterceptingVersusSeriesStore(_seriesStore) { AddFailure = unrelated };

        var thrown = await Assert.ThrowsAsync<ConflictException>(() => new CreateChallengeUseCase(store, _friendships, _catalog, _clock)
            .ExecuteAsync(new CreateChallengeRequest(challenger, opponent, "score-only", null, 3, null, Guid.NewGuid()), CancellationToken.None));

        Assert.Same(unrelated, thrown);
    }
}
