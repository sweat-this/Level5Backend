using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Competition;
using Level5.Domain.Competition;
using Level5.Domain.Ids;
using Level5.Domain.Social;
using Level5.Infrastructure.Competition;
using Level5.Infrastructure.Persistence.Repositories;
using Level5.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

/// <summary>
/// Certifies issue #10's create idempotency and challenge-transition race semantics end to end
/// against real Postgres: the real use cases over the real <see cref="VersusSeriesStore"/>, with
/// one <c>DbContext</c> per simulated request exactly as the API scopes them. The concurrent
/// tests release every request at once through a shared gate and assert only outcomes that must
/// hold under every possible interleaving, so they need no sleeps and cannot flake on timing.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChallengeConcurrencyTests(PostgresFixture fixture)
{
    private const int ConcurrentRequests = 8;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly SystemClock Clock = new();

    private static readonly FrozenRules DefaultRules = FrozenRules.Create(
        CompetitionProtocol.CurrentVersion, "score-only", 1, 1, "mode-score-only",
        InformationPolicy.SealedAttempt, alternatesFirstAttempt: false,
        [new ComparisonKey(ResultMetric.Score, MetricDirection.HigherWins)]);

    /// <summary>A lookup that misses once - the first request's view of the key before a racing request commits.</summary>
    private sealed class StaleFirstLookupStore(VersusSeriesStore inner) : IVersusSeriesStore
    {
        private bool _missed;

        public Task<VersusSeries?> FindByIdempotencyKeyAsync(PlayerId challengerId, Guid clientRequestId, CancellationToken cancellationToken)
        {
            if (!_missed)
            {
                _missed = true;
                return Task.FromResult<VersusSeries?>(null);
            }

            return inner.FindByIdempotencyKeyAsync(challengerId, clientRequestId, cancellationToken);
        }

        public Task<VersusSeries?> FindByIdAsync(VersusSeriesId id, CancellationToken cancellationToken) => inner.FindByIdAsync(id, cancellationToken);
        public Task AddAsync(VersusSeries series, Guid? clientRequestId, CancellationToken cancellationToken) => inner.AddAsync(series, clientRequestId, cancellationToken);
        public Task<PagedResult<SeriesSummary>> ListIncomingChallengeSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken) => inner.ListIncomingChallengeSummariesAsync(playerId, limit, cursor, cancellationToken);
        public Task<PagedResult<SeriesSummary>> ListOutgoingChallengeSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken) => inner.ListOutgoingChallengeSummariesAsync(playerId, limit, cursor, cancellationToken);
        public Task<PagedResult<SeriesSummary>> ListActiveSeriesSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken) => inner.ListActiveSeriesSummariesAsync(playerId, limit, cursor, cancellationToken);
        public Task<PagedResult<SeriesSummary>> ListCompletedSeriesSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken) => inner.ListCompletedSeriesSummariesAsync(playerId, limit, cursor, cancellationToken);
        public Task<PagedResult<SeriesSummary>> ListTerminalHistorySummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken) => inner.ListTerminalHistorySummariesAsync(playerId, limit, cursor, cancellationToken);
        public Task<IReadOnlyList<VersusSeriesId>> FindStalePendingChallengeIdsAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken) => inner.FindStalePendingChallengeIdsAsync(cutoff, batchSize, cancellationToken);
        public Task<bool> TrySaveAsync(VersusSeries series, long expectedRevision, CancellationToken cancellationToken) => inner.TrySaveAsync(series, expectedRevision, cancellationToken);
    }

    private async Task<(PlayerId Challenger, PlayerId Opponent)> SeedFriendsAsync(string label)
    {
        await using var db = fixture.CreateDbContext();
        var challenger = await PlayerSeeding.CreatePlayerAsync(db, label + "C", Now);
        var opponent = await PlayerSeeding.CreatePlayerAsync(db, label + "O", Now);
        await new FriendshipStore(db).AddFriendshipAsync(Friendship.Between(challenger, opponent, Now), CancellationToken.None);
        await db.SaveChangesAsync();
        return (challenger, opponent);
    }

    private async Task<PlayerId> SeedFriendOfAsync(PlayerId player, string label)
    {
        await using var db = fixture.CreateDbContext();
        var friend = await PlayerSeeding.CreatePlayerAsync(db, label, Now);
        await new FriendshipStore(db).AddFriendshipAsync(Friendship.Between(player, friend, Now), CancellationToken.None);
        await db.SaveChangesAsync();
        return friend;
    }

    /// <summary>Runs <paramref name="request"/> in its own DbContext, as one HTTP request would.</summary>
    private async Task<SeriesView> CreateAsync(CreateChallengeRequest request, Func<VersusSeriesStore, IVersusSeriesStore>? wrap = null)
    {
        await using var db = fixture.CreateDbContext();
        var store = new VersusSeriesStore(db);
        return await new CreateChallengeUseCase(wrap?.Invoke(store) ?? store, new FriendshipStore(db), new StaticRulesetCatalog(), Clock)
            .ExecuteAsync(request, CancellationToken.None);
    }

    private async Task<SeriesView> TransitionAsync(string command, VersusSeriesId seriesId, PlayerId challenger, PlayerId opponent)
    {
        await using var db = fixture.CreateDbContext();
        var store = new VersusSeriesStore(db);
        return command switch
        {
            "accept" => await new AcceptChallengeUseCase(store, Clock).ExecuteAsync(new AcceptChallengeRequest(opponent, seriesId), CancellationToken.None),
            "decline" => await new DeclineChallengeUseCase(store, Clock).ExecuteAsync(new DeclineChallengeRequest(opponent, seriesId), CancellationToken.None),
            "cancel" => await new CancelChallengeUseCase(store, Clock).ExecuteAsync(new CancelChallengeRequest(challenger, seriesId), CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
    }

    /// <summary>Starts every operation behind one gate and releases them together, capturing each outcome (result or exception).</summary>
    private static async Task<IReadOnlyList<Task<T>>> RunConcurrentlyAsync<T>(IEnumerable<Func<Task<T>>> operations)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = operations.Select(op => Task.Run(async () =>
        {
            await gate.Task;
            return await op();
        })).ToList();

        gate.SetResult();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // Individual outcomes are inspected by the caller.
        }

        return tasks;
    }

    private async Task<int> CountRowsAsync(PlayerId challenger, Guid clientRequestId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.VersusSeries.CountAsync(r => r.ChallengerId == challenger.Value && r.ClientRequestId == clientRequestId);
    }

    private static SeriesStatus StatusAfter(string command) => command switch
    {
        "accept" => SeriesStatus.Active,
        "decline" => SeriesStatus.Declined,
        "cancel" => SeriesStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    [Fact]
    public async Task Different_challengers_may_each_persist_a_series_under_the_same_clientRequestId()
    {
        await using var db = fixture.CreateDbContext();
        var challengerA = await PlayerSeeding.CreatePlayerAsync(db, "ShareA", Now);
        var challengerB = await PlayerSeeding.CreatePlayerAsync(db, "ShareB", Now);
        var opponent = await PlayerSeeding.CreatePlayerAsync(db, "ShareO", Now);
        var sharedKey = Guid.NewGuid();
        var store = new VersusSeriesStore(db);

        await store.AddAsync(VersusSeries.CreateChallenge(challengerA, opponent, SeriesFormat.BestOf(3), DefaultRules, Now), sharedKey, CancellationToken.None);
        await store.AddAsync(VersusSeries.CreateChallenge(challengerB, opponent, SeriesFormat.BestOf(3), DefaultRules, Now), sharedKey, CancellationToken.None);

        Assert.Equal(1, await CountRowsAsync(challengerA, sharedKey));
        Assert.Equal(1, await CountRowsAsync(challengerB, sharedKey));
    }

    [Fact]
    public async Task A_create_that_loses_the_insert_race_resolves_to_the_committed_series_through_the_real_unique_constraint()
    {
        var (challenger, opponent) = await SeedFriendsAsync("InsRace");
        var request = new CreateChallengeRequest(challenger, opponent, "score-only", null, 3, null, Guid.NewGuid());
        var winner = await CreateAsync(request);

        // The loser's lookup missed (it ran before the winner committed), so it really INSERTs and
        // really hits the (challenger_id, client_request_id) unique index.
        var loser = await CreateAsync(request, store => new StaleFirstLookupStore(store));

        Assert.Equal(winner.Id, loser.Id);
        Assert.Equal(1, await CountRowsAsync(challenger, request.ClientRequestId));
    }

    [Fact]
    public async Task Concurrent_identical_creates_persist_exactly_one_series_and_every_caller_gets_it()
    {
        var (challenger, opponent) = await SeedFriendsAsync("ConcSame");
        var request = new CreateChallengeRequest(challenger, opponent, "score-only", null, 3, null, Guid.NewGuid());

        var outcomes = await RunConcurrentlyAsync(Enumerable.Range(0, ConcurrentRequests).Select(_ => (Func<Task<SeriesView>>)(() => CreateAsync(request))));

        Assert.All(outcomes, t => Assert.True(t.IsCompletedSuccessfully, t.Exception?.ToString()));
        Assert.Single(outcomes.Select(t => t.Result.Id).Distinct());
        Assert.Equal(1, await CountRowsAsync(challenger, request.ClientRequestId));
    }

    [Fact]
    public async Task Concurrent_creates_reusing_one_key_for_different_requests_persist_one_series_and_conflict_the_rest()
    {
        var (challenger, opponentA) = await SeedFriendsAsync("ConcDiff");
        var opponentB = await SeedFriendOfAsync(challenger, "ConcDiffB");
        var key = Guid.NewGuid();
        var requestA = new CreateChallengeRequest(challenger, opponentA, "score-only", null, 3, null, key);
        var requestB = new CreateChallengeRequest(challenger, opponentB, "score-only", null, 3, null, key);

        var outcomes = await RunConcurrentlyAsync(new Func<Task<SeriesView>>[] { () => CreateAsync(requestA), () => CreateAsync(requestB) });

        var succeeded = Assert.Single(outcomes, t => t.IsCompletedSuccessfully);
        var failed = Assert.Single(outcomes, t => t.IsFaulted);
        Assert.IsType<ConflictException>(failed.Exception!.InnerException);
        Assert.Equal(1, await CountRowsAsync(challenger, key));

        await using var db = fixture.CreateDbContext();
        var stored = await new VersusSeriesStore(db).FindByIdempotencyKeyAsync(challenger, key, CancellationToken.None);
        Assert.Equal(succeeded.Result.Id, stored!.Id);
        Assert.Equal(succeeded.Result.OpponentId, stored.OpponentId);
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    [InlineData("cancel")]
    public async Task Concurrent_duplicate_transitions_all_succeed_with_exactly_one_committed_write(string command)
    {
        var (challenger, opponent) = await SeedFriendsAsync("Dup" + command);
        var created = await CreateAsync(new CreateChallengeRequest(challenger, opponent, "score-only", null, 3, null, Guid.NewGuid()));

        var outcomes = await RunConcurrentlyAsync(Enumerable.Range(0, ConcurrentRequests)
            .Select(_ => (Func<Task<SeriesView>>)(() => TransitionAsync(command, created.Id, challenger, opponent))));

        Assert.All(outcomes, t => Assert.True(t.IsCompletedSuccessfully, t.Exception?.ToString()));
        Assert.All(outcomes, t => Assert.Equal(StatusAfter(command), t.Result.Status));

        await using var db = fixture.CreateDbContext();
        var persisted = await new VersusSeriesStore(db).FindByIdAsync(created.Id, CancellationToken.None);
        Assert.Equal(StatusAfter(command), persisted!.Status);
        Assert.Equal(1, persisted.Revision);
    }

    [Theory]
    [InlineData("accept", "cancel")]
    [InlineData("accept", "decline")]
    [InlineData("decline", "cancel")]
    public async Task Concurrent_incompatible_transitions_have_exactly_one_winner_that_is_never_overwritten(string first, string second)
    {
        var (challenger, opponent) = await SeedFriendsAsync("Inc" + first[..2] + second[..2]);
        var created = await CreateAsync(new CreateChallengeRequest(challenger, opponent, "score-only", null, 3, null, Guid.NewGuid()));

        var outcomes = await RunConcurrentlyAsync(new Func<Task<SeriesView>>[]
        {
            () => TransitionAsync(first, created.Id, challenger, opponent),
            () => TransitionAsync(second, created.Id, challenger, opponent)
        });

        var winner = Assert.Single(outcomes, t => t.IsCompletedSuccessfully);
        var loser = Assert.Single(outcomes, t => t.IsFaulted);
        Assert.IsType<IllegalSeriesTransitionException>(loser.Exception!.InnerException);

        await using var db = fixture.CreateDbContext();
        var persisted = await new VersusSeriesStore(db).FindByIdAsync(created.Id, CancellationToken.None);
        Assert.Equal(winner.Result.Status, persisted!.Status);
        Assert.Equal(1, persisted.Revision);
    }
}
