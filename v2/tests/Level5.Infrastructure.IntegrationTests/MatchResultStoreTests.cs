using Level5.Application.Common;
using Level5.Domain.Ids;
using Level5.Domain.Results;
using Level5.Infrastructure.Persistence.Repositories;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class MatchResultStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static MatchResultMetrics DefaultMetrics(double totalPoints = 90) =>
        MatchResultMetrics.Of(new Dictionary<MatchResultMetric, double> { [MatchResultMetric.TotalPoints] = totalPoints });

    private static readonly MatchResultModifiers DefaultModifiers = MatchResultModifiers.Of(false, false, false, false);

    [Fact]
    public async Task Round_trips_a_result_through_relational_columns_and_the_jsonb_documents()
    {
        await using var writeDb = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(writeDb, "RoundTrip", Now);
        var metrics = MatchResultMetrics.Of(new Dictionary<MatchResultMetric, double>
        {
            [MatchResultMetric.TotalPoints] = 120,
            [MatchResultMetric.ShotsMade] = 8,
            [MatchResultMetric.CompletionTimeSeconds] = 42.5
        });
        var modifiers = MatchResultModifiers.Of(hardcore: true, trafficEnabled: false, enemiesEnabled: true, sniperEnabled: false);
        var result = MatchResult.Submit(player, Guid.NewGuid(), 1, 1, "hero", "1.2.3", "ios", metrics, modifiers, Now);

        await new MatchResultStore(writeDb).AddAsync(result, CancellationToken.None);

        await using var readDb = fixture.CreateDbContext();
        var reloaded = await new MatchResultStore(readDb).FindByClientResultIdAsync(player, result.ClientResultId, CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.Equal(result.Id, reloaded.Id);
        Assert.Equal(player, reloaded.PlayerId);
        Assert.Equal(1, reloaded.ModeId);
        Assert.Equal(1, reloaded.LevelId);
        Assert.Equal("hero", reloaded.CharacterId);
        Assert.Equal("1.2.3", reloaded.ClientVersion);
        Assert.Equal("ios", reloaded.Platform);
        Assert.Equal(metrics, reloaded.Metrics);
        Assert.Equal(modifiers, reloaded.Modifiers);
    }

    [Fact]
    public async Task A_metric_set_with_reordered_insertion_survives_a_reload_with_order_independent_equality()
    {
        await using var writeDb = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(writeDb, "OrderIndep", Now);
        var metrics = MatchResultMetrics.Of(new Dictionary<MatchResultMetric, double>
        {
            [MatchResultMetric.TotalPoints] = 90,
            [MatchResultMetric.EnemiesKilled] = 3,
            [MatchResultMetric.LongestStreak] = 5
        });
        var result = MatchResult.Submit(player, Guid.NewGuid(), 1, 1, "character", "1.0", "pc", metrics, DefaultModifiers, Now);

        await new MatchResultStore(writeDb).AddAsync(result, CancellationToken.None);

        await using var readDb = fixture.CreateDbContext();
        var reloaded = await new MatchResultStore(readDb).FindByClientResultIdAsync(player, result.ClientResultId, CancellationToken.None);

        var reordered = MatchResultMetrics.Of(new Dictionary<MatchResultMetric, double>
        {
            [MatchResultMetric.LongestStreak] = 5,
            [MatchResultMetric.TotalPoints] = 90,
            [MatchResultMetric.EnemiesKilled] = 3
        });
        Assert.Equal(reordered, reloaded!.Metrics);
    }

    [Fact]
    public async Task Server_assigned_CreatedAt_survives_a_reload()
    {
        await using var writeDb = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(writeDb, "CreatedAt", Now);
        var result = MatchResult.Submit(player, Guid.NewGuid(), 1, 1, "character", "1.0", "pc", DefaultMetrics(), DefaultModifiers, Now);

        await new MatchResultStore(writeDb).AddAsync(result, CancellationToken.None);

        await using var readDb = fixture.CreateDbContext();
        var reloaded = await new MatchResultStore(readDb).FindByClientResultIdAsync(player, result.ClientResultId, CancellationToken.None);

        // Postgres timestamptz has microsecond precision; a tick value finer than that is
        // truncated on write, so compare within a tolerance rather than exact tick equality.
        Assert.Equal(Now, reloaded!.CreatedAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task FindByClientResultIdAsync_is_scoped_to_the_player_that_submitted_it()
    {
        await using var db = fixture.CreateDbContext();
        var playerA = await PlayerSeeding.CreatePlayerAsync(db, "ScopeA", Now);
        var playerB = await PlayerSeeding.CreatePlayerAsync(db, "ScopeB", Now);
        var sharedKey = Guid.NewGuid();
        var resultA = MatchResult.Submit(playerA, sharedKey, 1, 1, "character", "1.0", "pc", DefaultMetrics(), DefaultModifiers, Now);

        var store = new MatchResultStore(db);
        await store.AddAsync(resultA, CancellationToken.None);

        var foundForA = await store.FindByClientResultIdAsync(playerA, sharedKey, CancellationToken.None);
        var foundForB = await store.FindByClientResultIdAsync(playerB, sharedKey, CancellationToken.None);

        Assert.NotNull(foundForA);
        Assert.Equal(resultA.Id, foundForA.Id);
        Assert.Null(foundForB);
    }

    [Fact]
    public async Task AddAsync_rejects_a_duplicate_clientResultId_for_the_same_player()
    {
        await using var db = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(db, "DupKey", Now);
        var clientResultId = Guid.NewGuid();
        var first = MatchResult.Submit(player, clientResultId, 1, 1, "character", "1.0", "pc", DefaultMetrics(), DefaultModifiers, Now);
        var second = MatchResult.Submit(player, clientResultId, 1, 2, "character", "1.0", "pc", DefaultMetrics(), DefaultModifiers, Now);

        var store = new MatchResultStore(db);
        await store.AddAsync(first, CancellationToken.None);

        await using var raceDb = fixture.CreateDbContext();
        var raceStore = new MatchResultStore(raceDb);
        await Assert.ThrowsAsync<ConflictException>(() => raceStore.AddAsync(second, CancellationToken.None));
    }

    [Fact]
    public async Task The_same_clientResultId_is_allowed_for_different_players()
    {
        await using var db = fixture.CreateDbContext();
        var playerA = await PlayerSeeding.CreatePlayerAsync(db, "MultiA", Now);
        var playerB = await PlayerSeeding.CreatePlayerAsync(db, "MultiB", Now);
        var sharedKey = Guid.NewGuid();
        var resultA = MatchResult.Submit(playerA, sharedKey, 1, 1, "character", "1.0", "pc", DefaultMetrics(), DefaultModifiers, Now);
        var resultB = MatchResult.Submit(playerB, sharedKey, 1, 1, "character", "1.0", "pc", DefaultMetrics(), DefaultModifiers, Now);

        var store = new MatchResultStore(db);
        await store.AddAsync(resultA, CancellationToken.None);
        await store.AddAsync(resultB, CancellationToken.None); // should not throw
    }
}
