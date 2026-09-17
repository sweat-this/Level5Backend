using Level5.Application.Common;
using Level5.Domain.Competition;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class VersusSeriesStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static readonly FrozenRules DefaultRules = FrozenRules.Create(
        CompetitionProtocol.CurrentVersion, "score-only", 1, 1, "mode-score-only",
        InformationPolicy.SealedAttempt, alternatesFirstAttempt: false,
        [new ComparisonKey(ResultMetric.Score, MetricDirection.HigherWins)]);

    [Fact]
    public async Task Round_trips_nested_attempt_state_through_the_jsonb_column()
    {
        await using var writeDb = fixture.CreateDbContext();
        var challenger = await PlayerSeeding.CreatePlayerAsync(writeDb, "Challenger", Now);
        var opponent = await PlayerSeeding.CreatePlayerAsync(writeDb, "Opponent", Now);
        var series = VersusSeries.CreateChallenge(challenger, opponent, SeriesFormat.BestOf(3), DefaultRules, Now);
        series.Accept(opponent, Now);
        var challengerAttempt = series.StartAttempt(challenger, 1, Now);
        var opponentAttempt = series.StartAttempt(opponent, 1, Now);
        series.CompleteAttempt(challenger, 1, challengerAttempt.Id, AttemptResult.OfScore(80), Now);
        series.CompleteAttempt(opponent, 1, opponentAttempt.Id, AttemptResult.OfScore(60), Now);

        var writeStore = new VersusSeriesStore(writeDb);
        await writeStore.AddAsync(series, clientRequestId: null, CancellationToken.None);
        await writeStore.TrySaveAsync(series, expectedRevision: 0, CancellationToken.None);

        await using var readDb = fixture.CreateDbContext();
        var readStore = new VersusSeriesStore(readDb);
        var reloaded = await readStore.FindByIdAsync(series.Id, CancellationToken.None);

        Assert.NotNull(reloaded);
        var round = Assert.Single(reloaded.Rounds);
        Assert.Equal(80d, round.ChallengerAttempt!.Result!.ValueOf(ResultMetric.Score));
        Assert.Equal(60d, round.OpponentAttempt!.Result!.ValueOf(ResultMetric.Score));
        Assert.Equal(AttemptStatus.Completed, round.OpponentAttempt.Status);
        Assert.Equal(challenger, reloaded.ChallengerId);
        Assert.Equal(opponent, reloaded.OpponentId);
    }

    [Fact]
    public async Task Frozen_rules_survive_a_reload_unchanged()
    {
        await using var writeDb = fixture.CreateDbContext();
        var challenger = await PlayerSeeding.CreatePlayerAsync(writeDb, "Challenger", Now);
        var opponent = await PlayerSeeding.CreatePlayerAsync(writeDb, "Opponent", Now);
        var series = VersusSeries.CreateChallenge(challenger, opponent, SeriesFormat.BestOf(3), DefaultRules, Now);

        await new VersusSeriesStore(writeDb).AddAsync(series, clientRequestId: null, CancellationToken.None);

        await using var readDb = fixture.CreateDbContext();
        var reloaded = await new VersusSeriesStore(readDb).FindByIdAsync(series.Id, CancellationToken.None);

        // The required invariant: FrozenRules(series at creation) == FrozenRules(series after
        // any later load). A later change to the live ruleset catalog must never retroactively
        // affect an already-created series - this is what a reload proving byte-for-byte
        // (structural) equality with the rules the series was created with actually verifies.
        Assert.Equal(DefaultRules, reloaded!.Rules);
    }

    [Fact]
    public async Task An_attempt_result_with_multiple_named_metrics_survives_a_reload_with_order_independent_equality()
    {
        await using var writeDb = fixture.CreateDbContext();
        var challenger = await PlayerSeeding.CreatePlayerAsync(writeDb, "Challenger", Now);
        var opponent = await PlayerSeeding.CreatePlayerAsync(writeDb, "Opponent", Now);
        var series = VersusSeries.CreateChallenge(challenger, opponent, SeriesFormat.BestOf(1), DefaultRules, Now);
        series.Accept(opponent, Now);
        var challengerAttempt = series.StartAttempt(challenger, 1, Now);

        // Constructed here directly against the domain (not through the score-only API DTO) to
        // prove the persistence representation itself - not just the current HTTP contract -
        // carries every named Protocol V1 metric, not only Score.
        var richResult = AttemptResult.Of(new Dictionary<ResultMetric, double>
        {
            [ResultMetric.Score] = 90,
            [ResultMetric.CompletionTimeSeconds] = 42.5,
            [ResultMetric.Accuracy] = 0.87
        });
        series.CompleteAttempt(challenger, 1, challengerAttempt.Id, richResult, Now);

        // AddAsync persists whatever state `series` is in right now - already including the
        // completed attempt above - so a single insert is enough to prove the round trip; no
        // separate TrySaveAsync is needed (and one wouldn't hit anyway: `series.Revision` is
        // already 3 at this point, past Accept/StartAttempt/CompleteAttempt).
        await new VersusSeriesStore(writeDb).AddAsync(series, clientRequestId: null, CancellationToken.None);

        await using var readDb = fixture.CreateDbContext();
        var reloaded = await new VersusSeriesStore(readDb).FindByIdAsync(series.Id, CancellationToken.None);

        var reloadedResult = reloaded!.Rounds.Single().ChallengerAttempt!.Result!;

        // A differently-ordered dictionary describing the same metrics must still compare equal -
        // metric equality is semantic, not tied to JSON property/insertion order.
        var reorderedExpectation = AttemptResult.Of(new Dictionary<ResultMetric, double>
        {
            [ResultMetric.Accuracy] = 0.87,
            [ResultMetric.Score] = 90,
            [ResultMetric.CompletionTimeSeconds] = 42.5
        });
        Assert.Equal(reorderedExpectation, reloadedResult);
    }

    [Fact]
    public async Task A_row_persisted_with_an_unsupported_schema_version_is_rejected_on_read()
    {
        await using var writeDb = fixture.CreateDbContext();
        var challenger = await PlayerSeeding.CreatePlayerAsync(writeDb, "Challenger", Now);
        var opponent = await PlayerSeeding.CreatePlayerAsync(writeDb, "Opponent", Now);
        var series = VersusSeries.CreateChallenge(challenger, opponent, SeriesFormat.BestOf(3), DefaultRules, Now);

        await new VersusSeriesStore(writeDb).AddAsync(series, clientRequestId: null, CancellationToken.None);

        // Simulates a schema-v1 row (pre-issue-#9: no frozen rules at all) landing in the table -
        // the read path must fail loudly and explicitly rather than silently misreading it as v2.
        // PascalCase, matching the real (unconfigured JsonSerializerOptions, case-sensitive)
        // shape VersusSeriesStore actually reads/writes - a lowercase literal would silently miss
        // the SchemaVersion property entirely and this test would pass for the wrong reason.
        const string legacyStateJson = """{"SchemaVersion":1,"Rounds":[]}""";
        await writeDb.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE competitive_series SET "StateJson" = {legacyStateJson}::jsonb WHERE "Id" = {series.Id.Value}""");

        await using var readDb = fixture.CreateDbContext();
        var readStore = new VersusSeriesStore(readDb);

        await Assert.ThrowsAsync<UnsupportedSeriesSchemaVersionException>(
            () => readStore.FindByIdAsync(series.Id, CancellationToken.None));
    }

    [Fact]
    public async Task TrySaveAsync_rejects_a_write_based_on_a_stale_revision()
    {
        await using var setupDb = fixture.CreateDbContext();
        var challenger = await PlayerSeeding.CreatePlayerAsync(setupDb, "Challenger", Now);
        var opponent = await PlayerSeeding.CreatePlayerAsync(setupDb, "Opponent", Now);
        var original = VersusSeries.CreateChallenge(challenger, opponent, SeriesFormat.BestOf(3), DefaultRules, Now);

        await new VersusSeriesStore(setupDb).AddAsync(original, clientRequestId: null, CancellationToken.None);

        // Two concurrent requests both load the series at revision 0.
        await using var dbA = fixture.CreateDbContext();
        await using var dbB = fixture.CreateDbContext();
        var storeA = new VersusSeriesStore(dbA);
        var storeB = new VersusSeriesStore(dbB);

        var seriesA = await storeA.FindByIdAsync(original.Id, CancellationToken.None);
        var seriesB = await storeB.FindByIdAsync(original.Id, CancellationToken.None);

        // Request A accepts and wins the race.
        seriesA!.Accept(opponent, Now);
        var savedA = await storeA.TrySaveAsync(seriesA, expectedRevision: 0, CancellationToken.None);

        // Request B (e.g. a cancel) was loaded from the same stale revision 0 and must not win.
        seriesB!.Cancel(challenger, Now);
        var savedB = await storeB.TrySaveAsync(seriesB, expectedRevision: 0, CancellationToken.None);

        Assert.True(savedA);
        Assert.False(savedB);

        await using var verifyDb = fixture.CreateDbContext();
        var final = await new VersusSeriesStore(verifyDb).FindByIdAsync(original.Id, CancellationToken.None);
        Assert.Equal(SeriesStatus.Active, final!.Status);
    }

    [Fact]
    public async Task A_losing_stale_completion_cannot_overwrite_the_winning_participants_accepted_completion()
    {
        await using var setupDb = fixture.CreateDbContext();
        var challenger = await PlayerSeeding.CreatePlayerAsync(setupDb, "Challenger", Now);
        var opponent = await PlayerSeeding.CreatePlayerAsync(setupDb, "Opponent", Now);
        var original = VersusSeries.CreateChallenge(challenger, opponent, SeriesFormat.BestOf(1), DefaultRules, Now);
        original.Accept(opponent, Now);
        var challengerAttempt = original.StartAttempt(challenger, 1, Now);
        var opponentAttempt = original.StartAttempt(opponent, 1, Now);

        await new VersusSeriesStore(setupDb).AddAsync(original, clientRequestId: null, CancellationToken.None);
        var expectedRevision = original.Revision;

        // Two concurrent requests - one per participant - both load the series at the same revision.
        await using var dbA = fixture.CreateDbContext();
        await using var dbB = fixture.CreateDbContext();
        var storeA = new VersusSeriesStore(dbA);
        var storeB = new VersusSeriesStore(dbB);

        var seriesA = await storeA.FindByIdAsync(original.Id, CancellationToken.None);
        var seriesB = await storeB.FindByIdAsync(original.Id, CancellationToken.None);

        // Request A (the challenger's completion) saves first and wins.
        seriesA!.CompleteAttempt(challenger, 1, challengerAttempt.Id, AttemptResult.OfScore(80), Now);
        var savedA = await storeA.TrySaveAsync(seriesA, expectedRevision, CancellationToken.None);

        // Request B (the opponent's completion) was loaded from the same now-stale revision and
        // must not silently win a last-write-wins race against A's already-persisted state.
        seriesB!.CompleteAttempt(opponent, 1, opponentAttempt.Id, AttemptResult.OfScore(60), Now);
        var savedB = await storeB.TrySaveAsync(seriesB, expectedRevision, CancellationToken.None);

        Assert.True(savedA);
        Assert.False(savedB);

        await using var verifyDb = fixture.CreateDbContext();
        var final = await new VersusSeriesStore(verifyDb).FindByIdAsync(original.Id, CancellationToken.None);
        var round = final!.Rounds.Single();
        Assert.Equal(AttemptStatus.Completed, round.ChallengerAttempt!.Status);
        // B's write lost the race entirely - the opponent's completion never reached the row, so
        // the series is not resolved and no terminal completion happened twice (or at all yet).
        Assert.Equal(AttemptStatus.NotStarted, round.OpponentAttempt!.Status);
        Assert.Equal(SeriesStatus.Active, final.Status);
    }

    [Fact]
    public async Task ListIncomingChallengesAsync_only_returns_pending_challenges_for_the_opponent()
    {
        await using var db = fixture.CreateDbContext();
        var challenger = await PlayerSeeding.CreatePlayerAsync(db, "Challenger", Now);
        var opponent = await PlayerSeeding.CreatePlayerAsync(db, "Opponent", Now);
        var unrelated = await PlayerSeeding.CreatePlayerAsync(db, "Unrelated", Now);

        var pending = VersusSeries.CreateChallenge(challenger, opponent, SeriesFormat.BestOf(3), DefaultRules, Now);
        var forSomeoneElse = VersusSeries.CreateChallenge(challenger, unrelated, SeriesFormat.BestOf(3), DefaultRules, Now);

        var store = new VersusSeriesStore(db);
        await store.AddAsync(pending, clientRequestId: null, CancellationToken.None);
        await store.AddAsync(forSomeoneElse, clientRequestId: null, CancellationToken.None);

        var incoming = await store.ListIncomingChallengesAsync(opponent, CancellationToken.None);

        var result = Assert.Single(incoming);
        Assert.Equal(pending.Id, result.Id);
    }

    [Fact]
    public async Task ListCompletedSeriesAsync_only_returns_completed_series_for_a_participant()
    {
        await using var db = fixture.CreateDbContext();
        var challenger = await PlayerSeeding.CreatePlayerAsync(db, "Challenger", Now);
        var opponent = await PlayerSeeding.CreatePlayerAsync(db, "Opponent", Now);
        var unrelated = await PlayerSeeding.CreatePlayerAsync(db, "Unrelated", Now);

        var completed = VersusSeries.CreateChallenge(challenger, opponent, SeriesFormat.BestOf(1), DefaultRules, Now);
        completed.Accept(opponent, Now);
        var completedChallengerAttempt = completed.StartAttempt(challenger, 1, Now);
        var completedOpponentAttempt = completed.StartAttempt(opponent, 1, Now);
        completed.CompleteAttempt(challenger, 1, completedChallengerAttempt.Id, AttemptResult.OfScore(80), Now);
        completed.CompleteAttempt(opponent, 1, completedOpponentAttempt.Id, AttemptResult.OfScore(60), Now);

        var stillPending = VersusSeries.CreateChallenge(challenger, opponent, SeriesFormat.BestOf(3), DefaultRules, Now);
        var declined = VersusSeries.CreateChallenge(challenger, opponent, SeriesFormat.BestOf(3), DefaultRules, Now);
        declined.Decline(opponent, Now);
        var forSomeoneElse = VersusSeries.CreateChallenge(challenger, unrelated, SeriesFormat.BestOf(1), DefaultRules, Now);
        forSomeoneElse.Accept(unrelated, Now);
        var elseChallengerAttempt = forSomeoneElse.StartAttempt(challenger, 1, Now);
        var elseUnrelatedAttempt = forSomeoneElse.StartAttempt(unrelated, 1, Now);
        forSomeoneElse.CompleteAttempt(challenger, 1, elseChallengerAttempt.Id, AttemptResult.OfScore(80), Now);
        forSomeoneElse.CompleteAttempt(unrelated, 1, elseUnrelatedAttempt.Id, AttemptResult.OfScore(60), Now);

        var store = new VersusSeriesStore(db);
        await store.AddAsync(completed, clientRequestId: null, CancellationToken.None);
        await store.AddAsync(stillPending, clientRequestId: null, CancellationToken.None);
        await store.AddAsync(declined, clientRequestId: null, CancellationToken.None);
        await store.AddAsync(forSomeoneElse, clientRequestId: null, CancellationToken.None);

        var opponentCompleted = await store.ListCompletedSeriesAsync(opponent, CancellationToken.None);
        var result = Assert.Single(opponentCompleted);
        Assert.Equal(completed.Id, result.Id);
    }

    [Fact]
    public async Task AddAsync_rejects_a_duplicate_clientRequestId_for_the_same_challenger()
    {
        await using var db = fixture.CreateDbContext();
        var challenger = await PlayerSeeding.CreatePlayerAsync(db, "Challenger", Now);
        var opponentA = await PlayerSeeding.CreatePlayerAsync(db, "OpponentA", Now);
        var opponentB = await PlayerSeeding.CreatePlayerAsync(db, "OpponentB", Now);
        var clientRequestId = Guid.NewGuid();
        var first = VersusSeries.CreateChallenge(challenger, opponentA, SeriesFormat.BestOf(3), DefaultRules, Now);
        var second = VersusSeries.CreateChallenge(challenger, opponentB, SeriesFormat.BestOf(3), DefaultRules, Now);

        var store = new VersusSeriesStore(db);
        await store.AddAsync(first, clientRequestId, CancellationToken.None);

        await using var raceDb = fixture.CreateDbContext();
        var raceStore = new VersusSeriesStore(raceDb);
        await Assert.ThrowsAsync<ConflictException>(() => raceStore.AddAsync(second, clientRequestId, CancellationToken.None));
    }

    [Fact]
    public async Task FindByIdempotencyKeyAsync_is_scoped_to_the_challenger_that_used_the_key()
    {
        await using var db = fixture.CreateDbContext();
        var challengerA = await PlayerSeeding.CreatePlayerAsync(db, "ChallengerA", Now);
        var challengerB = await PlayerSeeding.CreatePlayerAsync(db, "ChallengerB", Now);
        var opponent = await PlayerSeeding.CreatePlayerAsync(db, "Opponent", Now);
        var sharedKey = Guid.NewGuid();
        var seriesA = VersusSeries.CreateChallenge(challengerA, opponent, SeriesFormat.BestOf(3), DefaultRules, Now);

        var store = new VersusSeriesStore(db);
        await store.AddAsync(seriesA, sharedKey, CancellationToken.None);

        var foundForChallengerA = await store.FindByIdempotencyKeyAsync(challengerA, sharedKey, CancellationToken.None);
        var foundForChallengerB = await store.FindByIdempotencyKeyAsync(challengerB, sharedKey, CancellationToken.None);

        Assert.NotNull(foundForChallengerA);
        Assert.Equal(seriesA.Id, foundForChallengerA.Id);
        Assert.Null(foundForChallengerB);
    }
}
