using System.Text.Json;
using Level5.Application.Common;
using Level5.Application.Competition;
using Level5.Domain.Competition;
using Level5.Domain.Ids;
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
    public async Task ListIncomingChallengeSummariesAsync_only_returns_pending_challenges_for_the_opponent()
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

        var incoming = await store.ListIncomingChallengeSummariesAsync(opponent, limit: null, cursor: null, CancellationToken.None);

        var result = Assert.Single(incoming.Items);
        Assert.Equal(pending.Id, result.Id);
    }

    [Fact]
    public async Task ListCompletedSeriesSummariesAsync_only_returns_completed_series_for_a_participant()
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

        var opponentCompleted = await store.ListCompletedSeriesSummariesAsync(opponent, limit: null, cursor: null, CancellationToken.None);
        var result = Assert.Single(opponentCompleted.Items);
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

    [Fact]
    public async Task A_malformed_state_json_row_does_not_break_summary_listing_but_still_fails_detail_read()
    {
        await using var writeDb = fixture.CreateDbContext();
        var challenger = await PlayerSeeding.CreatePlayerAsync(writeDb, "MalformedChallenger", Now);
        var opponent = await PlayerSeeding.CreatePlayerAsync(writeDb, "MalformedOpponent", Now);
        var healthy = VersusSeries.CreateChallenge(challenger, opponent, SeriesFormat.BestOf(3), DefaultRules, Now);
        var malformed = VersusSeries.CreateChallenge(challenger, opponent, SeriesFormat.BestOf(3), DefaultRules, Now.AddSeconds(1));

        var store = new VersusSeriesStore(writeDb);
        await store.AddAsync(healthy, clientRequestId: null, CancellationToken.None);
        await store.AddAsync(malformed, clientRequestId: null, CancellationToken.None);

        // jsonb enforces syntactically valid JSON, so "malformed" here means structurally invalid
        // for the current schema, not unparseable text - "Rules" is a required member
        // VersusSeriesStateJson cannot deserialize without.
        const string malformedStateJson = """{"SchemaVersion":2}""";
        await writeDb.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE competitive_series SET "StateJson" = {malformedStateJson}::jsonb WHERE "Id" = {malformed.Id.Value}""");

        await using var readDb = fixture.CreateDbContext();
        var readStore = new VersusSeriesStore(readDb);

        var outgoing = await readStore.ListOutgoingChallengeSummariesAsync(challenger, limit: null, cursor: null, CancellationToken.None);
        Assert.Equal(2, outgoing.Items.Count);
        Assert.Contains(outgoing.Items, s => s.Id == healthy.Id);
        Assert.Contains(outgoing.Items, s => s.Id == malformed.Id);

        await Assert.ThrowsAsync<JsonException>(() => readStore.FindByIdAsync(malformed.Id, CancellationToken.None));
    }

    [Fact]
    public async Task An_unsupported_schema_version_row_does_not_break_summary_listing()
    {
        await using var writeDb = fixture.CreateDbContext();
        var challenger = await PlayerSeeding.CreatePlayerAsync(writeDb, "UnsupportedChallenger", Now);
        var opponent = await PlayerSeeding.CreatePlayerAsync(writeDb, "UnsupportedOpponent", Now);
        var healthy = VersusSeries.CreateChallenge(challenger, opponent, SeriesFormat.BestOf(3), DefaultRules, Now);
        var legacy = VersusSeries.CreateChallenge(challenger, opponent, SeriesFormat.BestOf(3), DefaultRules, Now.AddSeconds(1));

        var store = new VersusSeriesStore(writeDb);
        await store.AddAsync(healthy, clientRequestId: null, CancellationToken.None);
        await store.AddAsync(legacy, clientRequestId: null, CancellationToken.None);

        const string legacyStateJson = """{"SchemaVersion":1,"Rounds":[]}""";
        await writeDb.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE competitive_series SET "StateJson" = {legacyStateJson}::jsonb WHERE "Id" = {legacy.Id.Value}""");

        await using var readDb = fixture.CreateDbContext();
        var readStore = new VersusSeriesStore(readDb);

        var outgoing = await readStore.ListOutgoingChallengeSummariesAsync(challenger, limit: null, cursor: null, CancellationToken.None);
        Assert.Equal(2, outgoing.Items.Count);
        Assert.Contains(outgoing.Items, s => s.Id == healthy.Id);
        Assert.Contains(outgoing.Items, s => s.Id == legacy.Id);

        await Assert.ThrowsAsync<UnsupportedSeriesSchemaVersionException>(() => readStore.FindByIdAsync(legacy.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ListActiveSeriesSummariesAsync_orders_deterministically_by_CreatedAt_then_Id_descending()
    {
        await using var db = fixture.CreateDbContext();
        var a = await PlayerSeeding.CreatePlayerAsync(db, "OrderA", Now);
        var b = await PlayerSeeding.CreatePlayerAsync(db, "OrderB", Now);
        var store = new VersusSeriesStore(db);

        var created = new List<VersusSeries>();
        for (var i = 0; i < 4; i++)
        {
            var series = VersusSeries.CreateChallenge(a, b, SeriesFormat.BestOf(3), DefaultRules, Now.AddSeconds(i));
            series.Accept(b, Now.AddSeconds(i));
            await store.AddAsync(series, clientRequestId: null, CancellationToken.None);
            created.Add(series);
        }

        var page = await store.ListActiveSeriesSummariesAsync(a, limit: null, cursor: null, CancellationToken.None);

        var expectedOrder = created.OrderByDescending(s => s.CreatedAt).Select(s => s.Id).ToList();
        Assert.Equal(expectedOrder, page.Items.Select(i => i.Id).ToList());
    }

    [Fact]
    public async Task ListActiveSeriesSummariesAsync_defaults_to_a_bounded_page_when_no_limit_is_supplied()
    {
        await using var db = fixture.CreateDbContext();
        var a = await PlayerSeeding.CreatePlayerAsync(db, "DefaultPageA", Now);
        var b = await PlayerSeeding.CreatePlayerAsync(db, "DefaultPageB", Now);
        var store = new VersusSeriesStore(db);

        for (var i = 0; i < SeriesListPaging.DefaultLimit + 5; i++)
        {
            var series = VersusSeries.CreateChallenge(a, b, SeriesFormat.BestOf(3), DefaultRules, Now.AddSeconds(i));
            series.Accept(b, Now.AddSeconds(i));
            await store.AddAsync(series, clientRequestId: null, CancellationToken.None);
        }

        var page = await store.ListActiveSeriesSummariesAsync(a, limit: null, cursor: null, CancellationToken.None);

        Assert.Equal(SeriesListPaging.DefaultLimit, page.Items.Count);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task ListActiveSeriesSummariesAsync_clamps_a_requested_limit_above_the_maximum()
    {
        await using var db = fixture.CreateDbContext();
        var a = await PlayerSeeding.CreatePlayerAsync(db, "MaxPageA", Now);
        var b = await PlayerSeeding.CreatePlayerAsync(db, "MaxPageB", Now);
        var store = new VersusSeriesStore(db);

        for (var i = 0; i < SeriesListPaging.MaxLimit + 5; i++)
        {
            var series = VersusSeries.CreateChallenge(a, b, SeriesFormat.BestOf(3), DefaultRules, Now.AddSeconds(i));
            series.Accept(b, Now.AddSeconds(i));
            await store.AddAsync(series, clientRequestId: null, CancellationToken.None);
        }

        var page = await store.ListActiveSeriesSummariesAsync(a, limit: 1000, cursor: null, CancellationToken.None);

        Assert.Equal(SeriesListPaging.MaxLimit, page.Items.Count);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task Paginating_through_every_page_returns_each_row_exactly_once_with_no_gaps_or_duplicates()
    {
        await using var db = fixture.CreateDbContext();
        var a = await PlayerSeeding.CreatePlayerAsync(db, "PageBoundaryA", Now);
        var b = await PlayerSeeding.CreatePlayerAsync(db, "PageBoundaryB", Now);
        var store = new VersusSeriesStore(db);

        const int total = 25;
        var expectedIds = new List<VersusSeriesId>();
        for (var i = 0; i < total; i++)
        {
            var series = VersusSeries.CreateChallenge(a, b, SeriesFormat.BestOf(3), DefaultRules, Now.AddSeconds(i));
            series.Accept(b, Now.AddSeconds(i));
            await store.AddAsync(series, clientRequestId: null, CancellationToken.None);
            expectedIds.Add(series.Id);
        }

        var collected = new List<VersusSeriesId>();
        var pageSizes = new List<int>();
        string? cursor = null;
        do
        {
            var page = await store.ListActiveSeriesSummariesAsync(a, limit: 10, cursor, CancellationToken.None);
            pageSizes.Add(page.Items.Count);
            collected.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(new[] { 10, 10, 5 }, pageSizes);
        Assert.Equal(expectedIds.Count, collected.Distinct().Count());
        Assert.Equal(expectedIds.OrderBy(id => id.Value), collected.OrderBy(id => id.Value));
    }

    [Fact]
    public async Task An_invalid_pagination_cursor_is_rejected_with_a_validation_error()
    {
        await using var db = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(db, "InvalidCursorPlayer", Now);
        var store = new VersusSeriesStore(db);

        await Assert.ThrowsAsync<ValidationFailedException>(
            () => store.ListActiveSeriesSummariesAsync(player, limit: null, cursor: "not-a-real-cursor", CancellationToken.None));
    }

    [Fact]
    public async Task A_cursor_issued_by_one_list_query_is_rejected_by_a_different_one()
    {
        await using var db = fixture.CreateDbContext();
        var a = await PlayerSeeding.CreatePlayerAsync(db, "ScopeMismatchA", Now);
        var b = await PlayerSeeding.CreatePlayerAsync(db, "ScopeMismatchB", Now);
        var store = new VersusSeriesStore(db);

        for (var i = 0; i < 2; i++)
        {
            var series = VersusSeries.CreateChallenge(a, b, SeriesFormat.BestOf(3), DefaultRules, Now.AddSeconds(i));
            series.Accept(b, Now.AddSeconds(i));
            await store.AddAsync(series, clientRequestId: null, CancellationToken.None);
        }

        var activeFirstPage = await store.ListActiveSeriesSummariesAsync(a, limit: 1, cursor: null, CancellationToken.None);
        Assert.NotNull(activeFirstPage.NextCursor);

        // A well-formed cursor from ListActiveSeriesSummariesAsync must not be silently accepted
        // by ListOutgoingChallengeSummariesAsync - each list query's cursor is scoped to that query.
        await Assert.ThrowsAsync<ValidationFailedException>(
            () => store.ListOutgoingChallengeSummariesAsync(a, limit: 1, activeFirstPage.NextCursor, CancellationToken.None));
    }
}
