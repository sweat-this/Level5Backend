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

        var createResponse = await alice.Client.PostAsJsonAsync("/api/v2/series", new { opponentId = bob.PlayerId, totalGames = 1, rulesetId = "score-only", clientRequestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var series = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var seriesId = series.GetProperty("id").GetGuid();

        await bob.Client.PostAsync($"/api/v2/series/{seriesId}/accept", null);

        var aliceAttemptId = await StartAttemptAsync(alice, seriesId, 1);
        var bobAttemptId = await StartAttemptAsync(bob, seriesId, 1);

        await CompleteAttemptAsync(alice, seriesId, 1, aliceAttemptId, 90);
        var finalResponse = await CompleteAttemptRawAsync(bob, seriesId, 1, bobAttemptId, 10);
        var final = await finalResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        Assert.Equal("Completed", final.GetProperty("status").GetString());
        Assert.Equal(alice.PlayerId, final.GetProperty("winnerId").GetGuid());
    }

    [Fact]
    public async Task Start_returns_the_frozen_attempt_descriptor_required_by_unity()
    {
        var alice = await factory.RegisterNewPlayerAsync("AliceDesc");
        var bob = await factory.RegisterNewPlayerAsync("BobDesc");
        await BefriendAsync(alice, bob);
        var seriesId = await CreateAndAcceptSeriesAsync(alice, bob, totalGames: 3);

        var response = await alice.Client.PostAsync($"/api/v2/series/{seriesId}/games/1/attempts/start", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var descriptor = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        Assert.Equal(seriesId, descriptor.GetProperty("seriesId").GetGuid());
        Assert.NotEqual(Guid.Empty, descriptor.GetProperty("attemptId").GetGuid());
        Assert.Equal(1, descriptor.GetProperty("gameNumber").GetInt32());
        Assert.Equal(alice.PlayerId, descriptor.GetProperty("playerId").GetGuid());
        Assert.Equal(1, descriptor.GetProperty("competitionProtocolVersion").GetInt32());
        Assert.Equal("score-only", descriptor.GetProperty("rulesetId").GetString());
        Assert.Equal("SealedAttempt", descriptor.GetProperty("informationPolicy").GetString());
        Assert.Equal(3, descriptor.GetProperty("totalGames").GetInt32());
        Assert.Equal("Score", descriptor.GetProperty("requiredResultMetrics")[0].GetString());
        Assert.Equal("Score", descriptor.GetProperty("comparisonKeys")[0].GetProperty("metric").GetString());
        Assert.Equal("HigherWins", descriptor.GetProperty("comparisonKeys")[0].GetProperty("direction").GetString());
    }

    [Fact]
    public async Task Duplicate_start_returns_the_same_attempt_identity_and_descriptor()
    {
        var alice = await factory.RegisterNewPlayerAsync("AliceDup");
        var bob = await factory.RegisterNewPlayerAsync("BobDup");
        await BefriendAsync(alice, bob);
        var seriesId = await CreateAndAcceptSeriesAsync(alice, bob, totalGames: 3);

        var first = await alice.Client.PostAsync($"/api/v2/series/{seriesId}/games/1/attempts/start", null);
        var firstDescriptor = await first.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var second = await alice.Client.PostAsync($"/api/v2/series/{seriesId}/games/1/attempts/start", null);
        var secondDescriptor = await second.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        Assert.Equal(firstDescriptor.GetProperty("attemptId").GetGuid(), secondDescriptor.GetProperty("attemptId").GetGuid());
    }

    [Fact]
    public async Task Complete_with_a_missing_required_metric_is_rejected()
    {
        var alice = await factory.RegisterNewPlayerAsync("AliceMiss");
        var bob = await factory.RegisterNewPlayerAsync("BobMiss");
        await BefriendAsync(alice, bob);
        var seriesId = await CreateAndAcceptSeriesAsync(alice, bob, totalGames: 3);
        var attemptId = await StartAttemptAsync(alice, seriesId, 1);

        var response = await CompleteAttemptRawAsync(alice, seriesId, 1, attemptId, metrics: new Dictionary<string, double> { ["Accuracy"] = 90 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Complete_with_a_negative_metric_value_is_rejected_without_completing_the_attempt()
    {
        var alice = await factory.RegisterNewPlayerAsync("AliceNegative");
        var bob = await factory.RegisterNewPlayerAsync("BobNegative");
        await BefriendAsync(alice, bob);
        var seriesId = await CreateAndAcceptSeriesAsync(alice, bob, totalGames: 3);
        var attemptId = await StartAttemptAsync(alice, seriesId, 1);

        var response = await CompleteAttemptRawAsync(alice, seriesId, 1, attemptId, score: -1);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var view = await alice.Client.GetFromJsonAsync<JsonElement>($"/api/v2/series/{seriesId}", JsonOptions);
        Assert.Equal("NotStarted", view.GetProperty("games")[0].GetProperty("yourAttempt").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Complete_with_a_numeric_metric_identifier_is_rejected()
    {
        var alice = await factory.RegisterNewPlayerAsync("AliceNumeric");
        var bob = await factory.RegisterNewPlayerAsync("BobNumeric");
        await BefriendAsync(alice, bob);
        var seriesId = await CreateAndAcceptSeriesAsync(alice, bob, totalGames: 3);
        var attemptId = await StartAttemptAsync(alice, seriesId, 1);

        var response = await CompleteAttemptRawAsync(
            alice, seriesId, 1, attemptId, new Dictionary<string, double> { ["0"] = 50 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Complete_with_duplicate_canonical_metric_names_is_rejected()
    {
        var alice = await factory.RegisterNewPlayerAsync("AliceDuplicate");
        var bob = await factory.RegisterNewPlayerAsync("BobDuplicate");
        await BefriendAsync(alice, bob);
        var seriesId = await CreateAndAcceptSeriesAsync(alice, bob, totalGames: 3);
        var attemptId = await StartAttemptAsync(alice, seriesId, 1);

        var response = await CompleteAttemptRawAsync(alice, seriesId, 1, attemptId, new Dictionary<string, double>
        {
            ["Score"] = 50,
            ["score"] = 60
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Complete_with_the_wrong_attemptId_is_rejected()
    {
        var alice = await factory.RegisterNewPlayerAsync("AliceWrongId");
        var bob = await factory.RegisterNewPlayerAsync("BobWrongId");
        await BefriendAsync(alice, bob);
        var seriesId = await CreateAndAcceptSeriesAsync(alice, bob, totalGames: 3);
        await StartAttemptAsync(alice, seriesId, 1);

        var response = await CompleteAttemptRawAsync(alice, seriesId, 1, Guid.NewGuid(), 90);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_identical_completion_succeeds_idempotently()
    {
        var alice = await factory.RegisterNewPlayerAsync("AliceIdem");
        var bob = await factory.RegisterNewPlayerAsync("BobIdem");
        await BefriendAsync(alice, bob);
        var seriesId = await CreateAndAcceptSeriesAsync(alice, bob, totalGames: 3);
        var attemptId = await StartAttemptAsync(alice, seriesId, 1);

        var first = await CompleteAttemptRawAsync(alice, seriesId, 1, attemptId, 50);
        var retried = await CompleteAttemptRawAsync(alice, seriesId, 1, attemptId, 50);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
    }

    [Fact]
    public async Task Duplicate_conflicting_completion_returns_conflict_and_keeps_the_accepted_result()
    {
        var alice = await factory.RegisterNewPlayerAsync("AliceConf");
        var bob = await factory.RegisterNewPlayerAsync("BobConf");
        await BefriendAsync(alice, bob);
        var seriesId = await CreateAndAcceptSeriesAsync(alice, bob, totalGames: 3);
        var attemptId = await StartAttemptAsync(alice, seriesId, 1);

        await CompleteAttemptAsync(alice, seriesId, 1, attemptId, 50);
        var conflicting = await CompleteAttemptRawAsync(alice, seriesId, 1, attemptId, 999);

        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);

        var view = await alice.Client.GetAsync($"/api/v2/series/{seriesId}");
        var body = await view.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var game1 = body.GetProperty("games")[0];
        Assert.Equal(50d, game1.GetProperty("yourAttempt").GetProperty("result").GetProperty("Score").GetDouble());
    }

    [Fact]
    public async Task Simultaneous_completions_resolve_the_game_exactly_once()
    {
        var alice = await factory.RegisterNewPlayerAsync("AliceSim");
        var bob = await factory.RegisterNewPlayerAsync("BobSim");
        await BefriendAsync(alice, bob);
        var seriesId = await CreateAndAcceptSeriesAsync(alice, bob, totalGames: 1);
        var aliceAttemptId = await StartAttemptAsync(alice, seriesId, 1);
        var bobAttemptId = await StartAttemptAsync(bob, seriesId, 1);

        var responses = await Task.WhenAll(
            CompleteAttemptRawAsync(alice, seriesId, 1, aliceAttemptId, 90),
            CompleteAttemptRawAsync(bob, seriesId, 1, bobAttemptId, 10));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var finalView = await alice.Client.GetAsync($"/api/v2/series/{seriesId}");
        var body = await finalView.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("Completed", body.GetProperty("status").GetString());
        Assert.Equal(alice.PlayerId, body.GetProperty("winnerId").GetGuid());
        Assert.Single(body.GetProperty("games").EnumerateArray());
    }

    [Fact]
    public async Task Challenging_a_non_friend_is_rejected()
    {
        var alice = await factory.RegisterNewPlayerAsync("Alice2");
        var stranger = await factory.RegisterNewPlayerAsync("Stranger");

        var response = await alice.Client.PostAsJsonAsync("/api/v2/series", new { opponentId = stranger.PlayerId, totalGames = 3, rulesetId = "score-only", clientRequestId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Opponents_score_is_sealed_until_both_attempts_are_complete()
    {
        var alice = await factory.RegisterNewPlayerAsync("Alice3");
        var bob = await factory.RegisterNewPlayerAsync("Bob3");
        await BefriendAsync(alice, bob);

        var seriesId = await CreateAndAcceptSeriesAsync(alice, bob, totalGames: 3);

        var aliceAttemptId = await StartAttemptAsync(alice, seriesId, 1);
        await StartAttemptAsync(bob, seriesId, 1);
        await CompleteAttemptAsync(alice, seriesId, 1, aliceAttemptId, 77);

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
        Assert.Equal(JsonValueKind.Null, game1.GetProperty("opponentAttempt").GetProperty("result").ValueKind);
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

    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        var anonymous = factory.CreateClient();

        var create = await anonymous.PostAsJsonAsync("/api/v2/series", new { opponentId = Guid.NewGuid(), totalGames = 3, rulesetId = "score-only", clientRequestId = Guid.NewGuid() });
        var get = await anonymous.GetAsync($"/api/v2/series/{Guid.NewGuid()}");
        var incoming = await anonymous.GetAsync("/api/v2/series/incoming");
        var accept = await anonymous.PostAsync($"/api/v2/series/{Guid.NewGuid()}/accept", null);

        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, incoming.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, accept.StatusCode);
    }

    [Fact]
    public async Task Create_response_carries_server_resolved_frozen_rules()
    {
        var alice = await factory.RegisterNewPlayerAsync("Alice5");
        var bob = await factory.RegisterNewPlayerAsync("Bob5");
        await BefriendAsync(alice, bob);

        var createResponse = await alice.Client.PostAsJsonAsync(
            "/api/v2/series", new { opponentId = bob.PlayerId, totalGames = 3, rulesetId = "score-only", clientRequestId = Guid.NewGuid() });
        var series = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        var rules = series.GetProperty("rules");
        Assert.Equal("score-only", rules.GetProperty("rulesetId").GetString());
        Assert.Equal("SealedAttempt", rules.GetProperty("informationPolicy").GetString());
        Assert.Equal("Score", rules.GetProperty("comparisonKeys")[0].GetProperty("metric").GetString());
    }

    [Fact]
    public async Task Overposted_server_owned_fields_cannot_replace_server_authoritative_state()
    {
        var alice = await factory.RegisterNewPlayerAsync("Alice6");
        var bob = await factory.RegisterNewPlayerAsync("Bob6");
        var carol = await factory.RegisterNewPlayerAsync("Carol6");
        await BefriendAsync(alice, bob);

        var overpostedPayload = new Dictionary<string, object?>
        {
            ["opponentId"] = bob.PlayerId,
            ["totalGames"] = 3,
            ["rulesetId"] = "score-only",
            ["clientRequestId"] = Guid.NewGuid(),
            // Everything below is server-owned and must be silently ignored by model binding.
            ["challengerId"] = carol.PlayerId,
            ["status"] = "Completed",
            ["revision"] = 999,
            ["winnerId"] = carol.PlayerId,
            ["rules"] = new { rulesetId = "malicious-ruleset", informationPolicy = "OpenTarget" },
            ["comparisonKeys"] = new[] { new { metric = "Accuracy", direction = "LowerWins" } },
            ["games"] = new[] { new { gameNumber = 1 } },
            ["attempts"] = new[] { new { id = Guid.NewGuid() } },
            ["stateJson"] = "{\"hacked\":true}"
        };

        var createResponse = await alice.Client.PostAsJsonAsync("/api/v2/series", overpostedPayload);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var series = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        Assert.Equal(alice.PlayerId, series.GetProperty("challengerId").GetGuid());
        Assert.Equal("PendingAcceptance", series.GetProperty("status").GetString());
        Assert.Equal(0, series.GetProperty("revision").GetInt64());
        Assert.Equal(JsonValueKind.Null, series.GetProperty("winnerId").ValueKind);
        Assert.Equal("score-only", series.GetProperty("rules").GetProperty("rulesetId").GetString());
        Assert.Equal("SealedAttempt", series.GetProperty("rules").GetProperty("informationPolicy").GetString());
    }

    [Fact]
    public async Task Duplicate_create_with_the_same_clientRequestId_returns_the_original_series()
    {
        var alice = await factory.RegisterNewPlayerAsync("Alice7");
        var bob = await factory.RegisterNewPlayerAsync("Bob7");
        await BefriendAsync(alice, bob);
        var clientRequestId = Guid.NewGuid();

        var firstId = await CreateSeriesAsync(alice, bob, totalGames: 3, clientRequestId);
        var retriedId = await CreateSeriesAsync(alice, bob, totalGames: 3, clientRequestId);

        Assert.Equal(firstId, retriedId);

        var outgoing = await GetArrayAsync(alice, "/api/v2/series/outgoing");
        Assert.Single(outgoing.EnumerateArray(), s => s.GetProperty("id").GetGuid() == firstId);
    }

    [Fact]
    public async Task Reusing_a_clientRequestId_for_a_different_challenge_conflicts()
    {
        var alice = await factory.RegisterNewPlayerAsync("Alice8");
        var bob = await factory.RegisterNewPlayerAsync("Bob8");
        var carol = await factory.RegisterNewPlayerAsync("Carol8");
        await BefriendAsync(alice, bob);
        await BefriendAsync(alice, carol);
        var clientRequestId = Guid.NewGuid();

        await CreateSeriesAsync(alice, bob, totalGames: 3, clientRequestId);
        var conflicting = await alice.Client.PostAsJsonAsync(
            "/api/v2/series", new { opponentId = carol.PlayerId, totalGames = 3, rulesetId = "score-only", clientRequestId });

        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
    }

    [Fact]
    public async Task Missing_clientRequestId_is_rejected()
    {
        var alice = await factory.RegisterNewPlayerAsync("Alice9");
        var bob = await factory.RegisterNewPlayerAsync("Bob9");
        await BefriendAsync(alice, bob);

        var response = await alice.Client.PostAsJsonAsync("/api/v2/series", new { opponentId = bob.PlayerId, totalGames = 3, rulesetId = "score-only" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(9)]
    public async Task Unsupported_series_formats_are_rejected_through_http(int totalGames)
    {
        var alice = await factory.RegisterNewPlayerAsync($"AliceFmt{totalGames}");
        var bob = await factory.RegisterNewPlayerAsync($"BobFmt{totalGames}");
        await BefriendAsync(alice, bob);

        var response = await alice.Client.PostAsJsonAsync(
            "/api/v2/series", new { opponentId = bob.PlayerId, totalGames, rulesetId = "score-only", clientRequestId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Accept_retry_after_a_lost_response_is_safe()
    {
        var alice = await factory.RegisterNewPlayerAsync("Alice10");
        var bob = await factory.RegisterNewPlayerAsync("Bob10");
        await BefriendAsync(alice, bob);
        var seriesId = await CreateSeriesAsync(alice, bob, totalGames: 3);

        var firstAccept = await bob.Client.PostAsync($"/api/v2/series/{seriesId}/accept", null);
        var retriedAccept = await bob.Client.PostAsync($"/api/v2/series/{seriesId}/accept", null);

        Assert.Equal(HttpStatusCode.OK, firstAccept.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retriedAccept.StatusCode);
        var retried = await retriedAccept.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("Active", retried.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Challenger_cannot_accept_and_opponent_cannot_cancel()
    {
        var alice = await factory.RegisterNewPlayerAsync("Alice11");
        var bob = await factory.RegisterNewPlayerAsync("Bob11");
        await BefriendAsync(alice, bob);
        var seriesId = await CreateSeriesAsync(alice, bob, totalGames: 3);

        var challengerAccept = await alice.Client.PostAsync($"/api/v2/series/{seriesId}/accept", null);
        var opponentCancel = await bob.Client.PostAsync($"/api/v2/series/{seriesId}/cancel", null);

        Assert.Equal(HttpStatusCode.Forbidden, challengerAccept.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, opponentCancel.StatusCode);
    }

    [Fact]
    public async Task Opponent_decline_succeeds_through_http()
    {
        var alice = await factory.RegisterNewPlayerAsync("Alice13");
        var bob = await factory.RegisterNewPlayerAsync("Bob13");
        await BefriendAsync(alice, bob);
        var seriesId = await CreateSeriesAsync(alice, bob, totalGames: 3);

        var declineResponse = await bob.Client.PostAsync($"/api/v2/series/{seriesId}/decline", null);

        Assert.Equal(HttpStatusCode.OK, declineResponse.StatusCode);
        var declined = await declineResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("Declined", declined.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Challenger_cancel_succeeds_through_http()
    {
        var alice = await factory.RegisterNewPlayerAsync("Alice14");
        var bob = await factory.RegisterNewPlayerAsync("Bob14");
        await BefriendAsync(alice, bob);
        var seriesId = await CreateSeriesAsync(alice, bob, totalGames: 3);

        var cancelResponse = await alice.Client.PostAsync($"/api/v2/series/{seriesId}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);
        var cancelled = await cancelResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Incoming_outgoing_active_and_completed_lists_are_scoped_to_the_current_player_and_invisible_to_a_third_player()
    {
        var alice = await factory.RegisterNewPlayerAsync("Alice12");
        var bob = await factory.RegisterNewPlayerAsync("Bob12");
        var outsider = await factory.RegisterNewPlayerAsync("Outsider12");
        await BefriendAsync(alice, bob);

        var incomingSeriesId = await CreateSeriesAsync(alice, bob, totalGames: 3);
        var activeSeriesId = await CreateAndAcceptSeriesAsync(alice, bob, totalGames: 3);
        var completedSeriesId = await CreateAndAcceptSeriesAsync(alice, bob, totalGames: 1);
        var completedAliceAttempt = await StartAttemptAsync(alice, completedSeriesId, 1);
        var completedBobAttempt = await StartAttemptAsync(bob, completedSeriesId, 1);
        await CompleteAttemptAsync(alice, completedSeriesId, 1, completedAliceAttempt, 90);
        await CompleteAttemptAsync(bob, completedSeriesId, 1, completedBobAttempt, 10);

        var bobIncoming = await GetArrayAsync(bob, "/api/v2/series/incoming");
        Assert.Contains(bobIncoming.EnumerateArray(), s => s.GetProperty("id").GetGuid() == incomingSeriesId);

        var aliceOutgoing = await GetArrayAsync(alice, "/api/v2/series/outgoing");
        Assert.Contains(aliceOutgoing.EnumerateArray(), s => s.GetProperty("id").GetGuid() == incomingSeriesId);

        var aliceActive = await GetArrayAsync(alice, "/api/v2/series/active");
        Assert.Contains(aliceActive.EnumerateArray(), s => s.GetProperty("id").GetGuid() == activeSeriesId);

        var bobCompleted = await GetArrayAsync(bob, "/api/v2/series/completed");
        Assert.Contains(bobCompleted.EnumerateArray(), s => s.GetProperty("id").GetGuid() == completedSeriesId);
        Assert.DoesNotContain(bobCompleted.EnumerateArray(), s => s.GetProperty("id").GetGuid() == activeSeriesId);

        // A completed-list summary is sparse - it must never carry attempt/result data.
        var completedEntry = bobCompleted.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == completedSeriesId);
        Assert.False(completedEntry.TryGetProperty("games", out _));
        Assert.False(completedEntry.TryGetProperty("rules", out _));

        foreach (var route in new[] { "incoming", "outgoing", "active", "completed" })
        {
            var outsiderView = await GetArrayAsync(outsider, $"/api/v2/series/{route}");
            Assert.DoesNotContain(outsiderView.EnumerateArray(), s =>
                s.GetProperty("id").GetGuid() == incomingSeriesId ||
                s.GetProperty("id").GetGuid() == activeSeriesId ||
                s.GetProperty("id").GetGuid() == completedSeriesId);
        }
    }

    private static async Task<JsonElement> GetArrayAsync(RegisteredPlayer player, string path)
    {
        var response = await player.Client.GetAsync(path);
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
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
        var seriesId = await CreateSeriesAsync(challenger, opponent, totalGames);
        await opponent.Client.PostAsync($"/api/v2/series/{seriesId}/accept", null);
        return seriesId;
    }

    private static async Task<Guid> CreateSeriesAsync(RegisteredPlayer challenger, RegisteredPlayer opponent, int totalGames, Guid? clientRequestId = null)
    {
        var createResponse = await challenger.Client.PostAsJsonAsync(
            "/api/v2/series",
            new { opponentId = opponent.PlayerId, totalGames, rulesetId = "score-only", clientRequestId = clientRequestId ?? Guid.NewGuid() });
        var series = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        return series.GetProperty("id").GetGuid();
    }

    /// <summary>Starts an attempt and returns its authoritative <c>attemptId</c> from the returned descriptor.</summary>
    private static async Task<Guid> StartAttemptAsync(RegisteredPlayer player, Guid seriesId, int gameNumber)
    {
        var response = await player.Client.PostAsync($"/api/v2/series/{seriesId}/games/{gameNumber}/attempts/start", null);
        response.EnsureSuccessStatusCode();
        var descriptor = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return descriptor.GetProperty("attemptId").GetGuid();
    }

    private static Task<HttpResponseMessage> CompleteAttemptRawAsync(RegisteredPlayer player, Guid seriesId, int gameNumber, Guid attemptId, double score)
        => CompleteAttemptRawAsync(player, seriesId, gameNumber, attemptId, new Dictionary<string, double> { ["Score"] = score });

    private static Task<HttpResponseMessage> CompleteAttemptRawAsync(RegisteredPlayer player, Guid seriesId, int gameNumber, Guid attemptId, IReadOnlyDictionary<string, double> metrics)
        => player.Client.PostAsJsonAsync($"/api/v2/series/{seriesId}/games/{gameNumber}/attempts/{attemptId}/complete", new { metrics });

    private static async Task CompleteAttemptAsync(RegisteredPlayer player, Guid seriesId, int gameNumber, Guid attemptId, double score)
    {
        var response = await CompleteAttemptRawAsync(player, seriesId, gameNumber, attemptId, score);
        response.EnsureSuccessStatusCode();
    }
}
