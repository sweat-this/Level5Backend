using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Domain.Leaderboards;
using Level5.Domain.Results;
using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

/// <summary>
/// Certifies the real PostgreSQL leaderboard query (issue: leaderboard reads): ranking, filtering,
/// and keyset pagination all run as SQL against real JSONB documents and a real join to
/// player_profiles - not in-memory sorting of hydrated aggregates.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LeaderboardQueryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static async Task<Guid> SeedResultAsync(
        Level5V2DbContext db, PlayerId player, int modeId, double totalPoints, DateTimeOffset createdAt,
        bool hardcore = false, bool traffic = false, bool enemies = false, bool sniper = false,
        string characterId = "hero", int levelId = 1)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO match_results
                 ("Id", "PlayerId", "ClientResultId", "ModeId", "LevelId", "CharacterId", "ClientVersion", "Platform", "MetricsJson", "ModifiersJson", "CreatedAt")
             VALUES
                 ({id}, {player.Value}, {Guid.NewGuid()}, {modeId}, {levelId}, {characterId}, {"1.0.0"}, {"ios"},
                  {$"{{\"TotalPoints\":{totalPoints.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"}::jsonb,
                  {$"{{\"Hardcore\":{Bool(hardcore)},\"TrafficEnabled\":{Bool(traffic)},\"EnemiesEnabled\":{Bool(enemies)},\"SniperEnabled\":{Bool(sniper)}}}"}::jsonb,
                  {createdAt})
             """);
        return id;

        static string Bool(bool value) => value ? "true" : "false";
    }

    private static async Task SeedResultMissingRankingMetricAsync(Level5V2DbContext db, PlayerId player, int modeId, DateTimeOffset createdAt)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO match_results
                 ("Id", "PlayerId", "ClientResultId", "ModeId", "LevelId", "CharacterId", "ClientVersion", "Platform", "MetricsJson", "ModifiersJson", "CreatedAt")
             VALUES
                 ({Guid.NewGuid()}, {player.Value}, {Guid.NewGuid()}, {modeId}, {1}, {"hero"}, {"1.0.0"}, {"ios"},
                  {"{\"ShotsMade\":5}"}::jsonb,
                  {"{\"Hardcore\":false,\"TrafficEnabled\":false,\"EnemiesEnabled\":false,\"SniperEnabled\":false}"}::jsonb,
                  {createdAt})
             """);
    }

    private static LeaderboardQuerySpec Spec(
        int modeId, RankingDirection direction = RankingDirection.HigherWins,
        bool? hardcore = null, bool? traffic = null, bool? enemies = null, bool? sniper = null,
        int limit = 20, (double, DateTimeOffset, Guid)? cursor = null)
        => new(modeId, MatchResultMetric.TotalPoints, direction, hardcore, traffic, enemies, sniper, limit, cursor);

    [Fact]
    public async Task Higher_is_better_boards_rank_by_descending_value()
    {
        await using var db = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(db, "HQHigh", Now);
        var low = await SeedResultAsync(db, player, modeId: 401, totalPoints: 10, createdAt: Now);
        var high = await SeedResultAsync(db, player, modeId: 401, totalPoints: 90, createdAt: Now);
        var mid = await SeedResultAsync(db, player, modeId: 401, totalPoints: 50, createdAt: Now);

        var rows = await new LeaderboardQuery(db).ExecuteAsync(Spec(401, RankingDirection.HigherWins), CancellationToken.None);

        Assert.Equal([high, mid, low], rows.Select(r => r.MatchResultId));
    }

    [Fact]
    public async Task Lower_is_better_boards_rank_by_ascending_value()
    {
        await using var db = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(db, "HQLow", Now);
        var slow = await SeedResultAsync(db, player, modeId: 402, totalPoints: 90, createdAt: Now);
        var fast = await SeedResultAsync(db, player, modeId: 402, totalPoints: 10, createdAt: Now);
        var mid = await SeedResultAsync(db, player, modeId: 402, totalPoints: 50, createdAt: Now);

        var rows = await new LeaderboardQuery(db).ExecuteAsync(Spec(402, RankingDirection.LowerWins), CancellationToken.None);

        Assert.Equal([fast, mid, slow], rows.Select(r => r.MatchResultId));
    }

    [Fact]
    public async Task Ties_break_deterministically_by_CreatedAt_then_Id()
    {
        await using var db = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(db, "HQTie", Now);
        var earlier = await SeedResultAsync(db, player, modeId: 403, totalPoints: 50, createdAt: Now);
        var later = await SeedResultAsync(db, player, modeId: 403, totalPoints: 50, createdAt: Now.AddSeconds(5));

        var rows = await new LeaderboardQuery(db).ExecuteAsync(Spec(403), CancellationToken.None);

        Assert.Equal([earlier, later], rows.Select(r => r.MatchResultId));
    }

    [Fact]
    public async Task Rows_from_other_modes_are_excluded()
    {
        await using var db = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(db, "HQOtherMode", Now);
        var inBoard = await SeedResultAsync(db, player, modeId: 404, totalPoints: 50, createdAt: Now);
        await SeedResultAsync(db, player, modeId: 999, totalPoints: 999, createdAt: Now);

        var rows = await new LeaderboardQuery(db).ExecuteAsync(Spec(404), CancellationToken.None);

        Assert.Equal([inBoard], rows.Select(r => r.MatchResultId));
    }

    [Fact]
    public async Task A_row_missing_the_ranking_metric_is_excluded_defensively()
    {
        await using var db = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(db, "HQMissing", Now);
        var withMetric = await SeedResultAsync(db, player, modeId: 405, totalPoints: 50, createdAt: Now);
        await SeedResultMissingRankingMetricAsync(db, player, modeId: 405, createdAt: Now);

        var rows = await new LeaderboardQuery(db).ExecuteAsync(Spec(405), CancellationToken.None);

        Assert.Equal([withMetric], rows.Select(r => r.MatchResultId));
    }

    [Fact]
    public async Task No_filters_returns_every_row_regardless_of_modifiers()
    {
        await using var db = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(db, "HQNoFilter", Now);
        var plain = await SeedResultAsync(db, player, modeId: 406, totalPoints: 10, createdAt: Now);
        var hardcoreRow = await SeedResultAsync(db, player, modeId: 406, totalPoints: 20, createdAt: Now, hardcore: true);

        var rows = await new LeaderboardQuery(db).ExecuteAsync(Spec(406), CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.MatchResultId == plain);
        Assert.Contains(rows, r => r.MatchResultId == hardcoreRow);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_hardcore_filter_matches_exactly(bool hardcore)
    {
        await using var db = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(db, "HQHardcore" + hardcore, Now);
        var modeId = hardcore ? 4071 : 4072; // distinct per theory case - PostgresFixture's container is shared across the whole test collection.
        var matching = await SeedResultAsync(db, player, modeId, totalPoints: 10, createdAt: Now, hardcore: hardcore);
        await SeedResultAsync(db, player, modeId, totalPoints: 20, createdAt: Now, hardcore: !hardcore);

        var rows = await new LeaderboardQuery(db).ExecuteAsync(Spec(modeId, hardcore: hardcore), CancellationToken.None);

        Assert.Equal([matching], rows.Select(r => r.MatchResultId));
    }

    [Fact]
    public async Task Combined_modifier_filters_all_apply_together()
    {
        await using var db = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(db, "HQCombined", Now);
        var matching = await SeedResultAsync(db, player, modeId: 408, totalPoints: 10, createdAt: Now, hardcore: true, traffic: true, enemies: false, sniper: false);
        await SeedResultAsync(db, player, modeId: 408, totalPoints: 20, createdAt: Now, hardcore: true, traffic: false, enemies: false, sniper: false);
        await SeedResultAsync(db, player, modeId: 408, totalPoints: 30, createdAt: Now, hardcore: false, traffic: true, enemies: false, sniper: false);

        var rows = await new LeaderboardQuery(db)
            .ExecuteAsync(Spec(408, hardcore: true, traffic: true, enemies: false, sniper: false), CancellationToken.None);

        Assert.Equal([matching], rows.Select(r => r.MatchResultId));
    }

    [Fact]
    public async Task Public_player_identity_and_gameplay_fields_are_projected_correctly()
    {
        await using var db = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(db, "HQIdentity", Now);
        await SeedResultAsync(db, player, modeId: 409, totalPoints: 77, createdAt: Now, characterId: "captain", levelId: 42);

        var rows = await new LeaderboardQuery(db).ExecuteAsync(Spec(409), CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(player.Value, row.PlayerId);
        Assert.Equal("HQIdentity", row.DisplayName);
        Assert.False(string.IsNullOrEmpty(row.Tag));
        Assert.Equal("captain", row.CharacterId);
        Assert.Equal(42, row.LevelId);
        Assert.Equal(77, row.RankingValue);
    }

    [Fact]
    public async Task The_limit_plus_one_row_is_returned_so_the_caller_can_detect_a_next_page()
    {
        await using var db = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(db, "HQLimit", Now);
        for (var i = 0; i < 5; i++)
        {
            await SeedResultAsync(db, player, modeId: 410, totalPoints: i, createdAt: Now.AddSeconds(i));
        }

        var rows = await new LeaderboardQuery(db).ExecuteAsync(Spec(410, limit: 3), CancellationToken.None);

        Assert.Equal(4, rows.Count);
    }

    [Fact]
    public async Task Paging_through_with_the_cursor_returns_every_row_exactly_once_with_no_gaps()
    {
        await using var db = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(db, "HQPages", Now);
        var expectedOrder = new List<Guid>();
        for (var i = 0; i < 9; i++)
        {
            expectedOrder.Add(await SeedResultAsync(db, player, modeId: 411, totalPoints: 100 - i, createdAt: Now.AddSeconds(i)));
        }

        var collected = new List<Guid>();
        (double, DateTimeOffset, Guid)? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var rows = await new LeaderboardQuery(db).ExecuteAsync(Spec(411, limit: 4, cursor: cursor), CancellationToken.None);
            var hasMore = rows.Count > 4;
            var pageRows = hasMore ? rows.Take(4).ToList() : rows.ToList();
            collected.AddRange(pageRows.Select(r => r.MatchResultId));

            if (!hasMore)
            {
                break;
            }

            var last = pageRows[^1];
            cursor = (last.RankingValue, last.CreatedAt, last.MatchResultId);
        }

        Assert.Equal(expectedOrder, collected);
        Assert.Equal(expectedOrder.Count, collected.Distinct().Count());
    }

    [Fact]
    public async Task The_terminal_page_returns_no_extra_row()
    {
        await using var db = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(db, "HQTerminal", Now);
        await SeedResultAsync(db, player, modeId: 412, totalPoints: 1, createdAt: Now);
        await SeedResultAsync(db, player, modeId: 412, totalPoints: 2, createdAt: Now.AddSeconds(1));

        var rows = await new LeaderboardQuery(db).ExecuteAsync(Spec(412, limit: 5), CancellationToken.None);

        Assert.Equal(2, rows.Count);
    }
}
