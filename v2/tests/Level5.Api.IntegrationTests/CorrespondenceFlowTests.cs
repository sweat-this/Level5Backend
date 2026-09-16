using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Level5.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class CorrespondenceFlowTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Two_friends_can_play_a_full_best_of_one_series_through_http()
    {
        var alice = await factory.RegisterNewPlayerAsync("Alice");
        var bob = await factory.RegisterNewPlayerAsync("Bob");

        var sendResponse = await alice.Client.PostAsJsonAsync("/api/v2/friends/requests", new { toPlayerId = bob.PlayerId });
        var request = await sendResponse.Content.ReadFromJsonAsync<JsonElement>();
        var requestId = request.GetProperty("id").GetGuid();

        await bob.Client.PostAsync($"/api/v2/friends/requests/{requestId}/accept", null);

        var createResponse = await alice.Client.PostAsJsonAsync("/api/v2/series", new { opponentId = bob.PlayerId, totalGames = 1 });
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var series = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var seriesId = series.GetProperty("id").GetGuid();

        await bob.Client.PostAsync($"/api/v2/series/{seriesId}/accept", null);

        await alice.Client.PostAsync($"/api/v2/series/{seriesId}/games/1/attempts/start", null);
        await bob.Client.PostAsync($"/api/v2/series/{seriesId}/games/1/attempts/start", null);

        await alice.Client.PostAsJsonAsync($"/api/v2/series/{seriesId}/games/1/attempts/complete", new { score = 90 });
        var finalResponse = await bob.Client.PostAsJsonAsync($"/api/v2/series/{seriesId}/games/1/attempts/complete", new { score = 10 });
        var final = await finalResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        Assert.Equal("Completed", final.GetProperty("status").GetString());
        Assert.Equal(alice.PlayerId, final.GetProperty("winnerId").GetGuid());
    }

    [Fact]
    public async Task Challenging_a_non_friend_is_rejected()
    {
        var alice = await factory.RegisterNewPlayerAsync("Alice2");
        var stranger = await factory.RegisterNewPlayerAsync("Stranger");

        var response = await alice.Client.PostAsJsonAsync("/api/v2/series", new { opponentId = stranger.PlayerId, totalGames = 3 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Opponents_score_is_sealed_until_both_attempts_are_complete()
    {
        var alice = await factory.RegisterNewPlayerAsync("Alice3");
        var bob = await factory.RegisterNewPlayerAsync("Bob3");
        await BefriendAsync(alice, bob);

        var seriesId = await CreateAndAcceptSeriesAsync(alice, bob, totalGames: 3);

        await alice.Client.PostAsync($"/api/v2/series/{seriesId}/games/1/attempts/start", null);
        await bob.Client.PostAsync($"/api/v2/series/{seriesId}/games/1/attempts/start", null);
        await alice.Client.PostAsJsonAsync($"/api/v2/series/{seriesId}/games/1/attempts/complete", new { score = 77 });

        // Bob has not completed his own attempt yet - he must not be able to see Alice's score
        // through any read path: series detail, or any other field of the response body.
        var bobsView = await bob.Client.GetAsync($"/api/v2/series/{seriesId}");
        var body = await bobsView.Content.ReadAsStringAsync();
        var parsed = JsonSerializer.Deserialize<JsonElement>(body, JsonOptions);

        // Checked structurally rather than by substring-searching the raw JSON: the payload is
        // full of UUIDs and timestamps whose digits would randomly match a bare "77".
        Assert.False(ContainsValue(parsed, 77), $"Sealed score leaked into the response: {body}");

        var game1 = parsed.GetProperty("games")[0];
        Assert.Equal("Completed", game1.GetProperty("opponentAttempt").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, game1.GetProperty("opponentAttempt").GetProperty("score").ValueKind);
    }

    [Fact]
    public async Task A_non_participant_gets_not_found_rather_than_forbidden()
    {
        var alice = await factory.RegisterNewPlayerAsync("Alice4");
        var bob = await factory.RegisterNewPlayerAsync("Bob4");
        await BefriendAsync(alice, bob);
        var seriesId = await CreateAndAcceptSeriesAsync(alice, bob, totalGames: 3);

        var outsider = await factory.RegisterNewPlayerAsync("Outsider");
        var response = await outsider.Client.GetAsync($"/api/v2/series/{seriesId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>True if <paramref name="value"/> appears as a number (or a whole string) anywhere in the payload.</summary>
    private static bool ContainsValue(JsonElement element, int value) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetInt32(out var number) && number == value,
        JsonValueKind.String => element.GetString() == value.ToString(),
        JsonValueKind.Object => element.EnumerateObject().Any(property => ContainsValue(property.Value, value)),
        JsonValueKind.Array => element.EnumerateArray().Any(item => ContainsValue(item, value)),
        _ => false
    };

    private static async Task BefriendAsync(RegisteredPlayer a, RegisteredPlayer b)
    {
        var sendResponse = await a.Client.PostAsJsonAsync("/api/v2/friends/requests", new { toPlayerId = b.PlayerId });
        var request = await sendResponse.Content.ReadFromJsonAsync<JsonElement>();
        var requestId = request.GetProperty("id").GetGuid();
        await b.Client.PostAsync($"/api/v2/friends/requests/{requestId}/accept", null);
    }

    private static async Task<Guid> CreateAndAcceptSeriesAsync(RegisteredPlayer challenger, RegisteredPlayer opponent, int totalGames)
    {
        var createResponse = await challenger.Client.PostAsJsonAsync("/api/v2/series", new { opponentId = opponent.PlayerId, totalGames });
        var series = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var seriesId = series.GetProperty("id").GetGuid();
        await opponent.Client.PostAsync($"/api/v2/series/{seriesId}/accept", null);
        return seriesId;
    }
}
