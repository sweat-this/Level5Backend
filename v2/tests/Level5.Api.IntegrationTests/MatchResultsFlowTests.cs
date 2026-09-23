using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Level5.Domain.Results;
using Xunit;

namespace Level5.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class MatchResultsFlowTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static object ValidPayload(Guid clientResultId, double totalPoints = 90, object? modifiers = null) => new
    {
        clientResultId,
        modeId = 1,
        levelId = 1,
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
        Assert.Equal(1, body.GetProperty("modeId").GetInt32());
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
            modeId = 1,
            levelId = 1,
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
            modeId = 1,
            levelId = 1,
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
            modeId = 1,
            levelId = 1,
            characterId = "hero",
            clientVersion = "1.0.0",
            platform = "ios",
            metrics = new Dictionary<string, double>()
        };

        var response = await alice.Client.PostAsJsonAsync("/api/v2/match-results", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static object PayloadWith(Guid clientResultId, string characterId, string clientVersion, string platform) => new
    {
        clientResultId,
        modeId = 1,
        levelId = 1,
        characterId,
        clientVersion,
        platform,
        metrics = new Dictionary<string, double> { ["TotalPoints"] = 90 }
    };

    [Fact]
    public async Task A_characterId_at_exactly_the_maximum_length_is_accepted()
    {
        var alice = await factory.RegisterNewPlayerAsync("MRCharMax");
        var characterId = new string('c', MatchResultFieldLimits.CharacterIdMaxLength);

        var response = await alice.Client.PostAsJsonAsync(
            "/api/v2/match-results", PayloadWith(Guid.NewGuid(), characterId, "1.0.0", "ios"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_characterId_one_over_the_maximum_length_is_rejected_with_400_not_500()
    {
        var alice = await factory.RegisterNewPlayerAsync("MRCharOver");
        var characterId = new string('c', MatchResultFieldLimits.CharacterIdMaxLength + 1);

        var response = await alice.Client.PostAsJsonAsync(
            "/api/v2/match-results", PayloadWith(Guid.NewGuid(), characterId, "1.0.0", "ios"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_clientVersion_at_exactly_the_maximum_length_is_accepted()
    {
        var alice = await factory.RegisterNewPlayerAsync("MRVerMax");
        var clientVersion = new string('v', MatchResultFieldLimits.ClientVersionMaxLength);

        var response = await alice.Client.PostAsJsonAsync(
            "/api/v2/match-results", PayloadWith(Guid.NewGuid(), "hero", clientVersion, "ios"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_clientVersion_one_over_the_maximum_length_is_rejected_with_400_not_500()
    {
        var alice = await factory.RegisterNewPlayerAsync("MRVerOver");
        var clientVersion = new string('v', MatchResultFieldLimits.ClientVersionMaxLength + 1);

        var response = await alice.Client.PostAsJsonAsync(
            "/api/v2/match-results", PayloadWith(Guid.NewGuid(), "hero", clientVersion, "ios"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_platform_at_exactly_the_maximum_length_is_accepted()
    {
        var alice = await factory.RegisterNewPlayerAsync("MRPlatMax");
        var platform = new string('p', MatchResultFieldLimits.PlatformMaxLength);

        var response = await alice.Client.PostAsJsonAsync(
            "/api/v2/match-results", PayloadWith(Guid.NewGuid(), "hero", "1.0.0", platform));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_platform_one_over_the_maximum_length_is_rejected_with_400_not_500()
    {
        var alice = await factory.RegisterNewPlayerAsync("MRPlatOver");
        var platform = new string('p', MatchResultFieldLimits.PlatformMaxLength + 1);

        var response = await alice.Client.PostAsJsonAsync(
            "/api/v2/match-results", PayloadWith(Guid.NewGuid(), "hero", "1.0.0", platform));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_modeId_is_rejected(int modeId)
    {
        var alice = await factory.RegisterNewPlayerAsync("MRModeNonPos");
        var payload = new
        {
            clientResultId = Guid.NewGuid(),
            modeId,
            levelId = 1,
            characterId = "hero",
            clientVersion = "1.0.0",
            platform = "ios",
            metrics = new Dictionary<string, double> { ["TotalPoints"] = 90 }
        };

        var response = await alice.Client.PostAsJsonAsync("/api/v2/match-results", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_levelId_is_rejected(int levelId)
    {
        var alice = await factory.RegisterNewPlayerAsync("MRLevelNonPos");
        var payload = new
        {
            clientResultId = Guid.NewGuid(),
            modeId = 1,
            levelId,
            characterId = "hero",
            clientVersion = "1.0.0",
            platform = "ios",
            metrics = new Dictionary<string, double> { ["TotalPoints"] = 90 }
        };

        var response = await alice.Client.PostAsJsonAsync("/api/v2/match-results", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_or_whitespace_characterId_is_rejected(string characterId)
    {
        var alice = await factory.RegisterNewPlayerAsync("MRCharEmpty");

        var response = await alice.Client.PostAsJsonAsync(
            "/api/v2/match-results", PayloadWith(Guid.NewGuid(), characterId, "1.0.0", "ios"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_or_whitespace_clientVersion_is_rejected(string clientVersion)
    {
        var alice = await factory.RegisterNewPlayerAsync("MRVerEmpty");

        var response = await alice.Client.PostAsJsonAsync(
            "/api/v2/match-results", PayloadWith(Guid.NewGuid(), "hero", clientVersion, "ios"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_or_whitespace_platform_is_rejected(string platform)
    {
        var alice = await factory.RegisterNewPlayerAsync("MRPlatEmpty");

        var response = await alice.Client.PostAsJsonAsync(
            "/api/v2/match-results", PayloadWith(Guid.NewGuid(), "hero", "1.0.0", platform));

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
