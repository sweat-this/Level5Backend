using Level5.Domain.Competition;
using Level5.Domain.Ids;
using Level5.Infrastructure.Persistence.Repositories;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class VersusSeriesStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Round_trips_nested_attempt_state_through_the_jsonb_column()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        var series = VersusSeries.CreateChallenge(challenger, opponent, SeriesFormat.BestOf(3), Now);
        series.Accept(opponent, Now);
        series.StartAttempt(challenger, 1, Now);
        series.StartAttempt(opponent, 1, Now);
        series.CompleteAttempt(challenger, 1, Score.Of(80), Now);
        series.CompleteAttempt(opponent, 1, Score.Of(60), Now);

        await using var writeDb = fixture.CreateDbContext();
        var writeStore = new VersusSeriesStore(writeDb);
        await writeStore.AddAsync(series, CancellationToken.None);
        await writeStore.TrySaveAsync(series, expectedRevision: 0, CancellationToken.None);

        await using var readDb = fixture.CreateDbContext();
        var readStore = new VersusSeriesStore(readDb);
        var reloaded = await readStore.FindByIdAsync(series.Id, CancellationToken.None);

        Assert.NotNull(reloaded);
        var round = Assert.Single(reloaded!.Rounds);
        Assert.Equal(80, round.ChallengerAttempt!.Result!.Value.Value);
        Assert.Equal(60, round.OpponentAttempt!.Result!.Value.Value);
        Assert.Equal(AttemptStatus.Completed, round.OpponentAttempt.Status);
        Assert.Equal(challenger, reloaded.ChallengerId);
        Assert.Equal(opponent, reloaded.OpponentId);
    }

    [Fact]
    public async Task TrySaveAsync_rejects_a_write_based_on_a_stale_revision()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        var original = VersusSeries.CreateChallenge(challenger, opponent, SeriesFormat.BestOf(3), Now);

        await using var setupDb = fixture.CreateDbContext();
        await new VersusSeriesStore(setupDb).AddAsync(original, CancellationToken.None);

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
    public async Task ListIncomingChallengesAsync_only_returns_pending_challenges_for_the_opponent()
    {
        var challenger = PlayerId.New();
        var opponent = PlayerId.New();
        var unrelated = PlayerId.New();

        var pending = VersusSeries.CreateChallenge(challenger, opponent, SeriesFormat.BestOf(3), Now);
        var forSomeoneElse = VersusSeries.CreateChallenge(challenger, unrelated, SeriesFormat.BestOf(3), Now);

        await using var db = fixture.CreateDbContext();
        var store = new VersusSeriesStore(db);
        await store.AddAsync(pending, CancellationToken.None);
        await store.AddAsync(forSomeoneElse, CancellationToken.None);

        var incoming = await store.ListIncomingChallengesAsync(opponent, CancellationToken.None);

        var result = Assert.Single(incoming);
        Assert.Equal(pending.Id, result.Id);
    }
}
