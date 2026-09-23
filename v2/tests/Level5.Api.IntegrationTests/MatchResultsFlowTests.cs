using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Level5.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class MatchResultsFlowTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static object ValidPayload(Guid clientResultId, double totalPoints = 90, object? modifiers = null) => new
    {
        clientResultId,
        modeId = "arcade",
        levelId = "level-1",
        characterId = "hero",
        clientVersion = "1.0.0",
        platform = "ios",
        metrics = new Dictionary<string, double> { ["TotalPoints"] = totalPoints, ["ShotsMade"] = 8 },
        modifiers
    };

    [Fact]
    public async Task Unauthenticated_submission_is_rejected()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v2/match-results", ValidPayload(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_submission_is_accepted_and_belongs_to_the_authenticated_player()
    {
        var alice = await factory.RegisterNewPlayerAsync("MRAlice");

        var response = await alice.Client.PostAsJsonAsync("/api/v2/match-results", ValidPayload(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(alice.PlayerId, body.GetProperty("playerId").GetGuid());
        Assert.Equal("arcade", body.GetProperty("modeId").GetString());
        Assert.Equal(90d, body.GetProperty("metrics").GetProperty("TotalPoints").GetDouble());
        Assert.NotEqual(Guid.Empty, body.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_acting_player_cannot_be_spoofed_through_the_payload()
    {
        var alice = await factory.RegisterNewPlayerAsync("MRSpoof");
        var bob = await factory.RegisterNewPlayerAsync("MRSpoofVictim");

        var payload = new
        {
            playerId = bob.PlayerId, // not an accepted field - must be ignored, never trusted
            clientResultId = Guid.NewGuid(),
            modeId = "arcade",
            levelId = "level-1",
            characterId = "hero",
            clientVersion = "1.0.0",
            platform = "ios",
            metrics = new Dictionary<string, double> { ["TotalPoints"] = 50 }
        };

        var response = await alice.Client.PostAsJsonAsync("/api/v2/match-results", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(alice.PlayerId, body.GetProperty("playerId").GetGuid());
        Assert.NotEqual(bob.PlayerId, body.GetProperty("playerId").GetGuid());
    }

    [Fact]
    public async Task An_identical_replay_returns_the_same_result()
    {
        var alice = await factory.RegisterNewPlayerAsync("MRReplay");
        var clientResultId = Guid.NewGuid();

        var first = await alice.Client.PostAsJsonAsync("/api/v2/match-results", ValidPayload(clientResultId));
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        var second = await alice.Client.PostAsJsonAsync("/api/v2/match-results", ValidPayload(clientResultId));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        Assert.Equal(firstBody.GetProperty("id").GetGuid(), secondBody.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_conflicting_replay_is_rejected_with_409()
    {
        var alice = await factory.RegisterNewPlayerAsync("MRConflict");
        var clientResultId = Guid.NewGuid();

        await alice.Client.PostAsJsonAsync("/api/v2/match-results", ValidPayload(clientResultId, totalPoints: 90));
        var conflicting = await alice.Client.PostAsJsonAsync("/api/v2/match-results", ValidPayload(clientResultId, totalPoints: 999));

        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
    }

    [Fact]
    public async Task The_same_clientResultId_is_allowed_for_different_players()
    {
        var alice = await factory.RegisterNewPlayerAsync("MRMultiA");
        var bob = await factory.RegisterNewPlayerAsync("MRMultiB");
        var sharedKey = Guid.NewGuid();

        var aliceResponse = await alice.Client.PostAsJsonAsync("/api/v2/match-results", ValidPayload(sharedKey));
        var bobResponse = await bob.Client.PostAsJsonAsync("/api/v2/match-results", ValidPayload(sharedKey));

        Assert.Equal(HttpStatusCode.OK, aliceResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, bobResponse.StatusCode);
        var aliceBody = await aliceResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var bobBody = await bobResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.NotEqual(aliceBody.GetProperty("id").GetGuid(), bobBody.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task An_unknown_metric_name_is_rejected()
    {
        var alice = await factory.RegisterNewPlayerAsync("MRUnknownMetric");
        var payload = new
        {
            clientResultId = Guid.NewGuid(),
            modeId = "arcade",
            levelId = "level-1",
            characterId = "hero",
            clientVersion = "1.0.0",
            platform = "ios",
            metrics = new Dictionary<string, double> { ["NotARealMetric"] = 1 }
        };

        var response = await alice.Client.PostAsJsonAsync("/api/v2/match-results", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_negative_metric_value_is_rejected()
    {
        var alice = await factory.RegisterNewPlayerAsync("MRNegative");

        var response = await alice.Client.PostAsJsonAsync("/api/v2/match-results", ValidPayload(Guid.NewGuid(), totalPoints: -1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_empty_metrics_object_is_rejected()
    {
        var alice = await factory.RegisterNewPlayerAsync("MREmptyMetrics");
        var payload = new
        {
            clientResultId = Guid.NewGuid(),
            modeId = "arcade",
            levelId = "level-1",
            characterId = "hero",
            clientVersion = "1.0.0",
            platform = "ios",
            metrics = new Dictionary<string, double>()
        };

        var response = await alice.Client.PostAsJsonAsync("/api/v2/match-results", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Modifiers_round_trip_and_default_to_false_when_omitted()
    {
        var alice = await factory.RegisterNewPlayerAsync("MRModifiers");

        var withModifiers = await alice.Client.PostAsJsonAsync(
            "/api/v2/match-results",
            ValidPayload(Guid.NewGuid(), modifiers: new { hardcore = true, trafficEnabled = true, enemiesEnabled = false, sniperEnabled = false }));
        var withModifiersBody = await withModifiers.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.True(withModifiersBody.GetProperty("modifiers").GetProperty("hardcore").GetBoolean());
        Assert.True(withModifiersBody.GetProperty("modifiers").GetProperty("trafficEnabled").GetBoolean());
        Assert.False(withModifiersBody.GetProperty("modifiers").GetProperty("enemiesEnabled").GetBoolean());

        var withoutModifiers = await alice.Client.PostAsJsonAsync("/api/v2/match-results", ValidPayload(Guid.NewGuid()));
        var withoutModifiersBody = await withoutModifiers.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.False(withoutModifiersBody.GetProperty("modifiers").GetProperty("hardcore").GetBoolean());
        Assert.False(withoutModifiersBody.GetProperty("modifiers").GetProperty("trafficEnabled").GetBoolean());
        Assert.False(withoutModifiersBody.GetProperty("modifiers").GetProperty("enemiesEnabled").GetBoolean());
        Assert.False(withoutModifiersBody.GetProperty("modifiers").GetProperty("sniperEnabled").GetBoolean());
    }

    [Fact]
    public async Task No_private_account_data_is_serialized_in_the_response()
    {
        var alice = await factory.RegisterNewPlayerAsync("MRPrivacy");

        var response = await alice.Client.PostAsJsonAsync("/api/v2/match-results", ValidPayload(Guid.NewGuid()));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.False(body.TryGetProperty("accountId", out _));
        Assert.False(body.TryGetProperty("email", out _));
        Assert.False(body.TryGetProperty("username", out _));
    }
}
