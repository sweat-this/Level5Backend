using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Level5.Api.IntegrationTests;

/// <summary>
/// End-to-end leaderboard read coverage (issue: leaderboard reads): submits real match results
/// through the existing <c>/api/v2/match-results</c> endpoint, then certifies
/// <c>GET /api/v2/leaderboards/{modeId}</c> ranks, filters, paginates, and projects them correctly
/// through the real HTTP/auth/ProblemDetails pipeline. Every test uses a mode id no other test file
/// submits results for - <see cref="ApiFactory"/>'s Postgres container is shared across the whole
/// collection, so a shared mode id would let unrelated tests' rows leak into a ranking assertion.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LeaderboardsFlowTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static async Task SubmitAsync(
        RegisteredPlayer player, int modeId, double totalPoints,
        bool hardcore = false, bool traffic = false, bool enemies = false, bool sniper = false,
        string characterId = "hero", int levelId = 1)
    {
        var response = await player.Client.PostAsJsonAsync("/api/v2/match-results", new
        {
            clientResultId = Guid.NewGuid(),
            modeId,
            levelId,
            characterId,
            clientVersion = "1.0.0",
            platform = "ios",
            metrics = new Dictionary<string, double> { ["TotalPoints"] = totalPoints },
            modifiers = new { hardcore, trafficEnabled = traffic, enemiesEnabled = enemies, sniperEnabled = sniper }
        });
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Unauthenticated_leaderboard_read_is_rejected()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v2/leaderboards/1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unsupported_mode_is_rejected_with_a_deterministic_error_not_an_empty_board()
    {
        var alice = await factory.RegisterNewPlayerAsync("LBUnsupported");

        var response = await alice.Client.GetAsync("/api/v2/leaderboards/27");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("unsupported_leaderboard_mode", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_board_ranks_by_the_server_resolved_metric_and_direction_never_the_client()
    {
        const int modeId = 15;
        var alice = await factory.RegisterNewPlayerAsync("LBRankAlice");
        await SubmitAsync(alice, modeId, totalPoints: 10);
        await SubmitAsync(alice, modeId, totalPoints: 90);
        await SubmitAsync(alice, modeId, totalPoints: 50);

        var response = await alice.Client.GetAsync($"/api/v2/leaderboards/{modeId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("TotalPoints", page.GetProperty("metric").GetString());
        Assert.Equal("HigherWins", page.GetProperty("direction").GetString());
        var values = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("value").GetDouble()).ToList();
        Assert.Equal([90, 50, 10], values);
    }

    [Fact]
    public async Task Public_player_identity_is_projected_and_private_fields_are_never_serialized()
    {
        const int modeId = 16;
        var alice = await factory.RegisterNewPlayerAsync("LBPrivacyAlice");
        await SubmitAsync(alice, modeId, totalPoints: 42, characterId: "captain", levelId: 9);

        var response = await alice.Client.GetAsync($"/api/v2/leaderboards/{modeId}");

        var page = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(alice.PlayerId, item.GetProperty("player").GetProperty("playerId").GetGuid());
        Assert.Equal("LBPrivacyAlice", item.GetProperty("player").GetProperty("displayName").GetString());
        Assert.False(string.IsNullOrEmpty(item.GetProperty("player").GetProperty("tag").GetString()));
        Assert.Equal("captain", item.GetProperty("characterId").GetString());
        Assert.Equal(9, item.GetProperty("levelId").GetInt32());
        Assert.False(item.GetProperty("player").TryGetProperty("accountId", out _));
        Assert.False(item.GetProperty("player").TryGetProperty("username", out _));
        Assert.False(item.GetProperty("player").TryGetProperty("email", out _));
    }

    [Fact]
    public async Task Rows_from_other_modes_are_excluded()
    {
        const int inBoardMode = 17;
        const int otherMode = 998;
        var alice = await factory.RegisterNewPlayerAsync("LBOtherModeAlice");
        await SubmitAsync(alice, inBoardMode, totalPoints: 10);
        await SubmitAsync(alice, otherMode, totalPoints: 999);

        var response = await alice.Client.GetAsync($"/api/v2/leaderboards/{inBoardMode}");

        var page = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Single(page.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task The_hardcore_filter_matches_exactly()
    {
        const int modeId = 18;
        var alice = await factory.RegisterNewPlayerAsync("LBHardcoreAlice");
        await SubmitAsync(alice, modeId, totalPoints: 10, hardcore: true);
        await SubmitAsync(alice, modeId, totalPoints: 20, hardcore: false);

        var response = await alice.Client.GetAsync($"/api/v2/leaderboards/{modeId}?hardcore=true");

        var page = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var values = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("value").GetDouble()).ToList();
        Assert.Equal([10], values);
    }

    [Fact]
    public async Task Omitting_a_filter_does_not_constrain_that_modifier()
    {
        const int modeId = 19;
        var alice = await factory.RegisterNewPlayerAsync("LBNoFilterAlice");
        await SubmitAsync(alice, modeId, totalPoints: 10, hardcore: true);
        await SubmitAsync(alice, modeId, totalPoints: 20, hardcore: false);

        var response = await alice.Client.GetAsync($"/api/v2/leaderboards/{modeId}");

        var page = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Leaderboard_pagination_is_bounded_and_covers_every_row_with_no_duplicates_through_http()
    {
        const int modeId = 23;
        var alice = await factory.RegisterNewPlayerAsync("LBPageAlice");
        for (var i = 0; i < 5; i++)
        {
            await SubmitAsync(alice, modeId, totalPoints: i);
        }

        var firstPageResponse = await alice.Client.GetAsync($"/api/v2/leaderboards/{modeId}?limit=2");
        Assert.Equal(HttpStatusCode.OK, firstPageResponse.StatusCode);
        var firstPage = await firstPageResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(2, firstPage.GetProperty("items").GetArrayLength());
        Assert.Equal(2, firstPage.GetProperty("limit").GetInt32());
        var firstCursor = firstPage.GetProperty("nextCursor").GetString();
        Assert.NotNull(firstCursor);

        var collectedValues = new List<double>(firstPage.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("value").GetDouble()));
        var cursor = firstCursor;
        while (cursor is not null)
        {
            var response = await alice.Client.GetAsync($"/api/v2/leaderboards/{modeId}?limit=2&cursor={Uri.EscapeDataString(cursor)}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            collectedValues.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("value").GetDouble()));
            cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }

        Assert.Equal([4, 3, 2, 1, 0], collectedValues);
        Assert.Equal(5, collectedValues.Distinct().Count());
    }

    [Fact]
    public async Task A_malformed_pagination_cursor_is_rejected_through_http()
    {
        var alice = await factory.RegisterNewPlayerAsync("LBBadCursorAlice");

        var response = await alice.Client.GetAsync("/api/v2/leaderboards/1?cursor=not-a-real-cursor");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_cursor_issued_for_one_mode_is_rejected_against_a_different_mode_through_http()
    {
        const int firstMode = 24;
        const int secondMode = 26;
        var alice = await factory.RegisterNewPlayerAsync("LBCrossModeAlice");
        await SubmitAsync(alice, firstMode, totalPoints: 1);
        await SubmitAsync(alice, firstMode, totalPoints: 2);
        await SubmitAsync(alice, secondMode, totalPoints: 1);

        var firstResponse = await alice.Client.GetAsync($"/api/v2/leaderboards/{firstMode}?limit=1");
        var firstPage = await firstResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var cursor = firstPage.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        var crossModeResponse = await alice.Client.GetAsync($"/api/v2/leaderboards/{secondMode}?cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(HttpStatusCode.BadRequest, crossModeResponse.StatusCode);
    }

    [Fact]
    public async Task A_cursor_issued_without_a_filter_is_rejected_against_the_same_mode_with_a_filter_through_http()
    {
        // Mode 1, deliberately: this test asserts only that a cross-scope cursor is rejected, and
        // needs nothing from the board beyond "at least two rows exist" - which its own two
        // submissions guarantee. Every mode id used by a test that asserts an exact board must be
        // exclusive to that test (ApiFactory's Postgres container is shared by the whole
        // collection and is never reset), so this one deliberately does not consume one.
        const int modeId = 1;
        var alice = await factory.RegisterNewPlayerAsync("LBCrossFilterAlice");
        await SubmitAsync(alice, modeId, totalPoints: 1, hardcore: true);
        await SubmitAsync(alice, modeId, totalPoints: 2, hardcore: true);

        var unfilteredResponse = await alice.Client.GetAsync($"/api/v2/leaderboards/{modeId}?limit=1");
        var unfilteredPage = await unfilteredResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var cursor = unfilteredPage.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        var filteredResponse = await alice.Client.GetAsync($"/api/v2/leaderboards/{modeId}?limit=1&hardcore=true&cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(HttpStatusCode.BadRequest, filteredResponse.StatusCode);
    }

    [Fact]
    public async Task A_supported_mode_missing_its_required_metric_is_rejected_at_submission()
    {
        const int modeId = 1; // TotalPoints/HigherWins per StaticLeaderboardPolicyCatalog - no read happens here, so reuse is safe
        var alice = await factory.RegisterNewPlayerAsync("LBMissingMetricAlice");

        var response = await alice.Client.PostAsJsonAsync("/api/v2/match-results", new
        {
            clientResultId = Guid.NewGuid(),
            modeId,
            levelId = 1,
            characterId = "hero",
            clientVersion = "1.0.0",
            platform = "ios",
            metrics = new Dictionary<string, double> { ["ShotsMade"] = 5 }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
