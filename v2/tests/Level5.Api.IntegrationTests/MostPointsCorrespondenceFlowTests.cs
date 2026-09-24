using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Level5.Api.IntegrationTests;

/// <summary>
/// Freeze/descriptor coverage for the "most-points" ruleset (the one entry in
/// <c>StaticRulesetCatalog</c> Unity's own <c>DefaultCompetitiveRulesets</c> can actually resolve
/// and launch - "score-only" has no Unity counterpart). Proves the full server-authoritative chain
/// end to end through real HTTP: CreateChallenge freezes the exact three-key ordered comparison
/// Unity ships, StartAttempt's descriptor carries the same three metrics in the same order, and the
/// real domain's generic ordered-comparison algorithm (no mode-specific branches) actually applies
/// the Accuracy/ShotsAttempted tie-breaks when Score alone does not decide a game.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MostPointsCorrespondenceFlowTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Create_response_carries_the_exact_frozen_most_points_rules_unity_ships()
    {
        var alice = await factory.RegisterNewPlayerAsync("AliceMP1");
        var bob = await factory.RegisterNewPlayerAsync("BobMP1");
        await BefriendAsync(alice, bob);

        var createResponse = await alice.Client.PostAsJsonAsync(
            "/api/v2/series", new { opponentId = bob.PlayerId, totalGames = 3, rulesetId = "most-points", clientRequestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var series = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        var rules = series.GetProperty("rules");
        Assert.Equal("most-points", rules.GetProperty("rulesetId").GetString());
        Assert.Equal(1, rules.GetProperty("rulesetVersion").GetInt32());
        Assert.Equal(1, rules.GetProperty("minimumCompatibleVersion").GetInt32());
        Assert.Equal("most-points", rules.GetProperty("modeId").GetString());
        Assert.Equal("SealedAttempt", rules.GetProperty("informationPolicy").GetString());
        Assert.False(rules.GetProperty("alternatesFirstAttempt").GetBoolean());

        var comparisonKeys = rules.GetProperty("comparisonKeys");
        Assert.Equal(3, comparisonKeys.GetArrayLength());
        AssertComparisonKey(comparisonKeys[0], "Score", "HigherWins");
        AssertComparisonKey(comparisonKeys[1], "Accuracy", "HigherWins");
        AssertComparisonKey(comparisonKeys[2], "ShotsAttempted", "LowerWins");
    }

    [Fact]
    public async Task Start_returns_a_most_points_descriptor_requiring_all_three_metrics_in_order()
    {
        var alice = await factory.RegisterNewPlayerAsync("AliceMP2");
        var bob = await factory.RegisterNewPlayerAsync("BobMP2");
        await BefriendAsync(alice, bob);
        var seriesId = await CreateAndAcceptMostPointsSeriesAsync(alice, bob, totalGames: 3);

        var response = await alice.Client.PostAsync($"/api/v2/series/{seriesId}/games/1/attempts/start", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var descriptor = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        Assert.Equal("most-points", descriptor.GetProperty("rulesetId").GetString());
        Assert.Equal("most-points", descriptor.GetProperty("modeId").GetString());
        Assert.Equal("SealedAttempt", descriptor.GetProperty("informationPolicy").GetString());

        var requiredMetrics = descriptor.GetProperty("requiredResultMetrics");
        Assert.Equal(3, requiredMetrics.GetArrayLength());
        Assert.Equal("Score", requiredMetrics[0].GetString());
        Assert.Equal("Accuracy", requiredMetrics[1].GetString());
        Assert.Equal("ShotsAttempted", requiredMetrics[2].GetString());

        var comparisonKeys = descriptor.GetProperty("comparisonKeys");
        Assert.Equal(3, comparisonKeys.GetArrayLength());
        AssertComparisonKey(comparisonKeys[0], "Score", "HigherWins");
        AssertComparisonKey(comparisonKeys[1], "Accuracy", "HigherWins");
        AssertComparisonKey(comparisonKeys[2], "ShotsAttempted", "LowerWins");
    }

    [Fact]
    public async Task Tied_score_falls_through_to_the_higher_accuracy_tie_break()
    {
        var alice = await factory.RegisterNewPlayerAsync("AliceMP3");
        var bob = await factory.RegisterNewPlayerAsync("BobMP3");
        await BefriendAsync(alice, bob);
        var seriesId = await CreateAndAcceptMostPointsSeriesAsync(alice, bob, totalGames: 1);

        var aliceAttemptId = await StartAttemptAsync(alice, seriesId, 1);
        var bobAttemptId = await StartAttemptAsync(bob, seriesId, 1);

        // Same Score, Alice has the better Accuracy, ShotsAttempted tied - Accuracy alone must decide it.
        await CompleteAttemptAsync(alice, seriesId, 1, aliceAttemptId, score: 50, accuracy: 90, shotsAttempted: 20);
        var finalResponse = await CompleteAttemptRawAsync(bob, seriesId, 1, bobAttemptId, score: 50, accuracy: 70, shotsAttempted: 20);
        var final = await finalResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        Assert.Equal("Completed", final.GetProperty("status").GetString());
        Assert.Equal(alice.PlayerId, final.GetProperty("winnerId").GetGuid());
    }

    [Fact]
    public async Task Tied_score_and_accuracy_falls_through_to_the_fewer_shots_attempted_tie_break()
    {
        var alice = await factory.RegisterNewPlayerAsync("AliceMP4");
        var bob = await factory.RegisterNewPlayerAsync("BobMP4");
        await BefriendAsync(alice, bob);
        var seriesId = await CreateAndAcceptMostPointsSeriesAsync(alice, bob, totalGames: 1);

        var aliceAttemptId = await StartAttemptAsync(alice, seriesId, 1);
        var bobAttemptId = await StartAttemptAsync(bob, seriesId, 1);

        // Same Score and Accuracy - Alice needed fewer attempts to get there, so fewer wins.
        await CompleteAttemptAsync(alice, seriesId, 1, aliceAttemptId, score: 50, accuracy: 80, shotsAttempted: 15);
        var finalResponse = await CompleteAttemptRawAsync(bob, seriesId, 1, bobAttemptId, score: 50, accuracy: 80, shotsAttempted: 20);
        var final = await finalResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        Assert.Equal("Completed", final.GetProperty("status").GetString());
        Assert.Equal(alice.PlayerId, final.GetProperty("winnerId").GetGuid());
    }

    private static void AssertComparisonKey(JsonElement key, string expectedMetric, string expectedDirection)
    {
        Assert.Equal(expectedMetric, key.GetProperty("metric").GetString());
        Assert.Equal(expectedDirection, key.GetProperty("direction").GetString());
    }

    private static async Task BefriendAsync(RegisteredPlayer a, RegisteredPlayer b)
    {
        var sendResponse = await a.Client.PostAsJsonAsync("/api/v2/friends/requests", new { toPlayerId = b.PlayerId });
        var request = await sendResponse.Content.ReadFromJsonAsync<JsonElement>();
        var requestId = request.GetProperty("id").GetGuid();
        await b.Client.PostAsync($"/api/v2/friends/requests/{requestId}/accept", null);
    }

    private static async Task<Guid> CreateAndAcceptMostPointsSeriesAsync(RegisteredPlayer challenger, RegisteredPlayer opponent, int totalGames)
    {
        var createResponse = await challenger.Client.PostAsJsonAsync(
            "/api/v2/series",
            new { opponentId = opponent.PlayerId, totalGames, rulesetId = "most-points", clientRequestId = Guid.NewGuid() });
        var series = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var seriesId = series.GetProperty("id").GetGuid();
        await opponent.Client.PostAsync($"/api/v2/series/{seriesId}/accept", null);
        return seriesId;
    }

    private static async Task<Guid> StartAttemptAsync(RegisteredPlayer player, Guid seriesId, int gameNumber)
    {
        var response = await player.Client.PostAsync($"/api/v2/series/{seriesId}/games/{gameNumber}/attempts/start", null);
        response.EnsureSuccessStatusCode();
        var descriptor = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return descriptor.GetProperty("attemptId").GetGuid();
    }

    private static Task<HttpResponseMessage> CompleteAttemptRawAsync(
        RegisteredPlayer player, Guid seriesId, int gameNumber, Guid attemptId, double score, double accuracy, double shotsAttempted)
    {
        var metrics = new Dictionary<string, double>
        {
            ["Score"] = score,
            ["Accuracy"] = accuracy,
            ["ShotsAttempted"] = shotsAttempted
        };
        return player.Client.PostAsJsonAsync($"/api/v2/series/{seriesId}/games/{gameNumber}/attempts/{attemptId}/complete", new { metrics });
    }

    private static async Task CompleteAttemptAsync(
        RegisteredPlayer player, Guid seriesId, int gameNumber, Guid attemptId, double score, double accuracy, double shotsAttempted)
    {
        var response = await CompleteAttemptRawAsync(player, seriesId, gameNumber, attemptId, score, accuracy, shotsAttempted);
        response.EnsureSuccessStatusCode();
    }
}
