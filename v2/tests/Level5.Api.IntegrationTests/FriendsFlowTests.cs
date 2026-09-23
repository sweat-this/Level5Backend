using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Level5.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class FriendsFlowTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Send_request_is_visible_as_outgoing_to_the_sender_incoming_to_the_recipient_and_invisible_to_a_third_player()
    {
        var alice = await factory.RegisterNewPlayerAsync("FAlice");
        var bob = await factory.RegisterNewPlayerAsync("FBob");
        var carol = await factory.RegisterNewPlayerAsync("FCarol");

        var sendResponse = await alice.Client.PostAsJsonAsync("/api/v2/friends/requests", new { toPlayerId = bob.PlayerId });
        Assert.Equal(HttpStatusCode.OK, sendResponse.StatusCode);
        var request = await sendResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var requestId = request.GetProperty("id").GetGuid();

        var aliceOutgoing = await GetArrayAsync(alice, "/api/v2/friends/requests/outgoing");
        var aliceOutgoingEntry = Assert.Single(aliceOutgoing.EnumerateArray(), r => r.GetProperty("id").GetGuid() == requestId);
        // Outgoing: otherPlayer is the recipient, not the sender.
        Assert.Equal(bob.PlayerId, aliceOutgoingEntry.GetProperty("otherPlayer").GetProperty("playerId").GetGuid());
        Assert.Equal("FBob", aliceOutgoingEntry.GetProperty("otherPlayer").GetProperty("displayName").GetString());
        Assert.False(string.IsNullOrEmpty(aliceOutgoingEntry.GetProperty("otherPlayer").GetProperty("tag").GetString()));

        var bobIncoming = await GetArrayAsync(bob, "/api/v2/friends/requests/incoming");
        var bobIncomingEntry = Assert.Single(bobIncoming.EnumerateArray(), r => r.GetProperty("id").GetGuid() == requestId);
        // Incoming: otherPlayer is the sender, not the recipient.
        Assert.Equal(alice.PlayerId, bobIncomingEntry.GetProperty("otherPlayer").GetProperty("playerId").GetGuid());
        Assert.Equal("FAlice", bobIncomingEntry.GetProperty("otherPlayer").GetProperty("displayName").GetString());

        // Never leak private account fields alongside the public player summary.
        Assert.False(bobIncomingEntry.GetProperty("otherPlayer").TryGetProperty("accountId", out _));
        Assert.False(bobIncomingEntry.GetProperty("otherPlayer").TryGetProperty("email", out _));
        Assert.False(bobIncomingEntry.GetProperty("otherPlayer").TryGetProperty("username", out _));

        var carolIncoming = await GetArrayAsync(carol, "/api/v2/friends/requests/incoming");
        var carolOutgoing = await GetArrayAsync(carol, "/api/v2/friends/requests/outgoing");
        Assert.DoesNotContain(carolIncoming.EnumerateArray(), r => r.GetProperty("id").GetGuid() == requestId);
        Assert.DoesNotContain(carolOutgoing.EnumerateArray(), r => r.GetProperty("id").GetGuid() == requestId);
    }

    [Fact]
    public async Task Only_the_recipient_can_accept_or_decline_and_a_third_player_cannot_cancel_either()
    {
        var alice = await factory.RegisterNewPlayerAsync("GAlice");
        var bob = await factory.RegisterNewPlayerAsync("GBob");
        var carol = await factory.RegisterNewPlayerAsync("GCarol");

        var requestId = await SendAsync(alice, bob.PlayerId);

        var carolAccept = await carol.Client.PostAsync($"/api/v2/friends/requests/{requestId}/accept", null);
        Assert.Equal(HttpStatusCode.Forbidden, carolAccept.StatusCode);

        var carolDecline = await carol.Client.PostAsync($"/api/v2/friends/requests/{requestId}/decline", null);
        Assert.Equal(HttpStatusCode.Forbidden, carolDecline.StatusCode);

        var carolCancel = await carol.Client.PostAsync($"/api/v2/friends/requests/{requestId}/cancel", null);
        Assert.Equal(HttpStatusCode.Forbidden, carolCancel.StatusCode);

        var aliceAccept = await alice.Client.PostAsync($"/api/v2/friends/requests/{requestId}/accept", null);
        Assert.Equal(HttpStatusCode.Forbidden, aliceAccept.StatusCode);

        var bobAccept = await bob.Client.PostAsync($"/api/v2/friends/requests/{requestId}/accept", null);
        Assert.Equal(HttpStatusCode.NoContent, bobAccept.StatusCode);
    }

    [Fact]
    public async Task The_sender_can_cancel_a_still_pending_request()
    {
        var alice = await factory.RegisterNewPlayerAsync("HAlice");
        var bob = await factory.RegisterNewPlayerAsync("HBob");

        var requestId = await SendAsync(alice, bob.PlayerId);

        var cancelResponse = await alice.Client.PostAsync($"/api/v2/friends/requests/{requestId}/cancel", null);
        Assert.Equal(HttpStatusCode.NoContent, cancelResponse.StatusCode);

        var bobIncoming = await GetArrayAsync(bob, "/api/v2/friends/requests/incoming");
        Assert.DoesNotContain(bobIncoming.EnumerateArray(), r => r.GetProperty("id").GetGuid() == requestId);
    }

    [Fact]
    public async Task An_accepted_friendship_appears_to_both_players_and_removal_disappears_it_from_both()
    {
        var alice = await factory.RegisterNewPlayerAsync("IAlice");
        var bob = await factory.RegisterNewPlayerAsync("IBob");

        var requestId = await SendAsync(alice, bob.PlayerId);
        await bob.Client.PostAsync($"/api/v2/friends/requests/{requestId}/accept", null);

        var aliceFriends = await GetArrayAsync(alice, "/api/v2/friends");
        var bobFriends = await GetArrayAsync(bob, "/api/v2/friends");
        Assert.Contains(aliceFriends.EnumerateArray(), f => f.GetProperty("playerId").GetGuid() == bob.PlayerId);
        Assert.Contains(bobFriends.EnumerateArray(), f => f.GetProperty("playerId").GetGuid() == alice.PlayerId);

        var removeResponse = await alice.Client.DeleteAsync($"/api/v2/friends/{bob.PlayerId}");
        Assert.Equal(HttpStatusCode.NoContent, removeResponse.StatusCode);

        var aliceFriendsAfter = await GetArrayAsync(alice, "/api/v2/friends");
        var bobFriendsAfter = await GetArrayAsync(bob, "/api/v2/friends");
        Assert.DoesNotContain(aliceFriendsAfter.EnumerateArray(), f => f.GetProperty("playerId").GetGuid() == bob.PlayerId);
        Assert.DoesNotContain(bobFriendsAfter.EnumerateArray(), f => f.GetProperty("playerId").GetGuid() == alice.PlayerId);
    }

    [Fact]
    public async Task Removing_a_friendship_that_does_not_exist_is_not_found()
    {
        var alice = await factory.RegisterNewPlayerAsync("JAlice");
        var stranger = await factory.RegisterNewPlayerAsync("JStranger");

        var response = await alice.Client.DeleteAsync($"/api/v2/friends/{stranger.PlayerId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Sending_a_duplicate_or_crossed_direction_request_returns_conflict()
    {
        var alice = await factory.RegisterNewPlayerAsync("KAlice");
        var bob = await factory.RegisterNewPlayerAsync("KBob");

        await SendAsync(alice, bob.PlayerId);

        var duplicateSameDirection = await alice.Client.PostAsJsonAsync("/api/v2/friends/requests", new { toPlayerId = bob.PlayerId });
        Assert.Equal(HttpStatusCode.Conflict, duplicateSameDirection.StatusCode);

        var crossedDirection = await bob.Client.PostAsJsonAsync("/api/v2/friends/requests", new { toPlayerId = alice.PlayerId });
        Assert.Equal(HttpStatusCode.Conflict, crossedDirection.StatusCode);
    }

    [Fact]
    public async Task Removing_a_friendship_prevents_a_new_friend_required_challenge_but_does_not_touch_the_old_one()
    {
        var alice = await factory.RegisterNewPlayerAsync("LAlice");
        var bob = await factory.RegisterNewPlayerAsync("LBob");

        var requestId = await SendAsync(alice, bob.PlayerId);
        await bob.Client.PostAsync($"/api/v2/friends/requests/{requestId}/accept", null);

        var firstChallenge = await alice.Client.PostAsJsonAsync("/api/v2/series", new { opponentId = bob.PlayerId, totalGames = 1, rulesetId = "score-only", clientRequestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, firstChallenge.StatusCode);
        var firstSeries = await firstChallenge.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var firstSeriesId = firstSeries.GetProperty("id").GetGuid();

        await alice.Client.DeleteAsync($"/api/v2/friends/{bob.PlayerId}");

        var secondChallenge = await alice.Client.PostAsJsonAsync("/api/v2/series", new { opponentId = bob.PlayerId, totalGames = 1, rulesetId = "score-only", clientRequestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, secondChallenge.StatusCode);

        // The already-created series from before the removal must remain untouched/accessible.
        var stillReadable = await alice.Client.GetAsync($"/api/v2/series/{firstSeriesId}");
        Assert.Equal(HttpStatusCode.OK, stillReadable.StatusCode);
    }

    private static async Task<Guid> SendAsync(RegisteredPlayer sender, Guid toPlayerId)
    {
        var response = await sender.Client.PostAsJsonAsync("/api/v2/friends/requests", new { toPlayerId });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> GetArrayAsync(RegisteredPlayer player, string path)
    {
        var response = await player.Client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(body, JsonOptions);
    }
}
