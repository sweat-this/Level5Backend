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
    public Task<IReadOnlyList<VersusSeries>> ListIncomingChallengesAsync(PlayerId playerId, CancellationToken cancellationToken) => inner.ListIncomingChallengesAsync(playerId, cancellationToken);
    public Task<IReadOnlyList<VersusSeries>> ListOutgoingChallengesAsync(PlayerId playerId, CancellationToken cancellationToken) => inner.ListOutgoingChallengesAsync(playerId, cancellationToken);
    public Task<IReadOnlyList<VersusSeries>> ListActiveSeriesAsync(PlayerId playerId, CancellationToken cancellationToken) => inner.ListActiveSeriesAsync(playerId, cancellationToken);
    public Task<IReadOnlyList<VersusSeries>> ListCompletedSeriesAsync(PlayerId playerId, CancellationToken cancellationToken) => inner.ListCompletedSeriesAsync(playerId, cancellationToken);
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
    public Task<IReadOnlyList<VersusSeries>> ListIncomingChallengesAsync(PlayerId playerId, CancellationToken cancellationToken) => inner.ListIncomingChallengesAsync(playerId, cancellationToken);
    public Task<IReadOnlyList<VersusSeries>> ListOutgoingChallengesAsync(PlayerId playerId, CancellationToken cancellationToken) => inner.ListOutgoingChallengesAsync(playerId, cancellationToken);
    public Task<IReadOnlyList<VersusSeries>> ListActiveSeriesAsync(PlayerId playerId, CancellationToken cancellationToken) => inner.ListActiveSeriesAsync(playerId, cancellationToken);
    public Task<IReadOnlyList<VersusSeries>> ListCompletedSeriesAsync(PlayerId playerId, CancellationToken cancellationToken) => inner.ListCompletedSeriesAsync(playerId, cancellationToken);

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
/// Exercises the races the ADR calls out explicitly. The store-level "which write actually wins
/// a stale-revision race" behavior is proven against real Postgres in the Infrastructure
/// integration tests; this layer's own job is narrower and just as important: every mutating use
/// case must turn a lost race (TrySaveAsync returning false) into a client-visible conflict
/// response instead of silently discarding the loser's request.
/// </summary>
public class ConcurrencyTests
{
    private readonly InMemoryVersusSeriesStore _seriesStore = new();
    private readonly InMemoryFriendshipStore _friendships = new();
    private readonly FakeRulesetCatalog _catalog = new();
    private readonly FakeClock _clock = new();

    private async Task<(VersusSeriesId SeriesId, PlayerId Challenger, PlayerId Opponent)> SeedPendingChallengeAsync()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        await _friendships.AddFriendshipAsync(Friendship.Between(challenger, opponent, _clock.UtcNow), CancellationToken.None);

        var view = await new CreateChallengeUseCase(_seriesStore, _friendships, _catalog, _clock)
            .ExecuteAsync(new CreateChallengeRequest(challenger, opponent, "score-only", null, 3, null, Guid.NewGuid()), CancellationToken.None);

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
}
