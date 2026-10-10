using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Level5.Application.Abstractions;
using Level5.Application.BloodMoney;
using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;
using Level5.Domain.Social;
using Level5.Infrastructure.BloodMoney;
using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Level5.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class BloodMoneyChallengeFlowTests(ApiFactory factory)
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly BloodMoneyChallengeTimingPolicy Timing = new(TimeSpan.FromHours(1), TimeSpan.FromHours(2));
    private static string Path(Guid id) => $"/api/v2/games/blood-money/challenges/{id}";

    [Theory]
    [InlineData(2)][InlineData(3)][InlineData(4)]
    public async Task Every_participant_can_read_the_canonical_projection_without_private_identity_or_financial_writes(int count)
    {
        var (view, players) = await Seed(count);
        var before = await Snapshot();
        foreach (var player in players)
        {
            using var response = await player.Client.GetAsync(Path(view.ChallengeId.Value));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(new[] { "acceptanceDeadlineAt", "activatedAt", "challengeId", "createdAt", "creatorPlayerId",
                "gameplayDeadlineAt", "participants", "revision", "rulesetId", "rulesetVersion", "stakePerParticipant", "status", "terminalAt" },
                body.EnumerateObject().Select(property => property.Name).Order());
            Assert.Equal(view.ChallengeId.Value, body.GetProperty("challengeId").GetGuid());
            Assert.Equal(view.CreatorPlayerId.Value, body.GetProperty("creatorPlayerId").GetGuid());
            Assert.Equal(view.Status.ToString(), body.GetProperty("status").GetString());
            Assert.Equal(view.StakePerParticipant, body.GetProperty("stakePerParticipant").GetInt64());
            Assert.Equal(view.RulesetId, body.GetProperty("rulesetId").GetString());
            Assert.Equal(view.RulesetVersion, body.GetProperty("rulesetVersion").GetInt32());
            Assert.Equal(view.CreatedAt, body.GetProperty("createdAt").GetDateTimeOffset());
            Assert.Equal(view.AcceptanceDeadlineAt, body.GetProperty("acceptanceDeadlineAt").GetDateTimeOffset());
            Assert.Equal(view.Revision, body.GetProperty("revision").GetInt64());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("activatedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("gameplayDeadlineAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("terminalAt").ValueKind);
            AssertRoster(view, body);
            var text = await response.Content.ReadAsStringAsync();
            foreach (var excluded in new[] { "accountId", "email", "clientRequestId", "terminalActorPlayerId", "reservation", "postings", "balance", "refreshToken" })
                Assert.DoesNotContain(excluded, text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(before, await Snapshot());
    }

    [Theory]
    [InlineData(BloodMoneyChallengeStatus.PendingAcceptance)]
    [InlineData(BloodMoneyChallengeStatus.Active)]
    [InlineData(BloodMoneyChallengeStatus.Declined)]
    [InlineData(BloodMoneyChallengeStatus.Cancelled)]
    [InlineData(BloodMoneyChallengeStatus.Expired)]
    public async Task Reads_preserve_every_existing_lifecycle_state_and_immutable_roster(BloodMoneyChallengeStatus status)
    {
        var (view, players) = await Seed(2, status);
        var before = await Snapshot();
        foreach (var player in players)
        {
            using var response = await player.Client.GetAsync(Path(view.ChallengeId.Value));
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(status.ToString(), body.GetProperty("status").GetString());
            Assert.Equal(view.Revision, body.GetProperty("revision").GetInt64());
            AssertTimestamp(view.ActivatedAt, body.GetProperty("activatedAt"));
            AssertTimestamp(view.GameplayDeadlineAt, body.GetProperty("gameplayDeadlineAt"));
            AssertTimestamp(view.TerminalAt, body.GetProperty("terminalAt"));
            AssertRoster(view, body);
        }
        Assert.Equal(before, await Snapshot());
    }

    [Fact]
    public async Task Reading_past_deadline_does_not_expire_or_release_a_pending_challenge()
    {
        var (view, players) = await Seed(2, createdAt: DateTimeOffset.UtcNow.AddDays(-2));
        var before = await Snapshot();
        using var response = await players[1].Client.GetAsync(Path(view.ChallengeId.Value));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PendingAcceptance", body.GetProperty("status").GetString());
        Assert.Equal(view.Revision, body.GetProperty("revision").GetInt64());
        Assert.Equal(before, await Snapshot());
    }

    [Fact]
    public async Task Authentication_is_required_and_spoofed_player_inputs_cannot_reveal_a_foreign_challenge()
    {
        var (view, players) = await Seed(2);
        using var anonymous = factory.CreateClient();
        using var unauthenticated = await anonymous.GetAsync(Path(view.ChallengeId.Value));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Empty(await unauthenticated.Content.ReadAsStringAsync());
        var outsider = await factory.RegisterNewPlayerAsync("ChallengeOutsider");
        foreach (var id in new[] { view.ChallengeId.Value, Guid.NewGuid(), Guid.Empty })
        {
            using var response = await outsider.Client.GetAsync(Path(id) + $"?playerId={players[0].PlayerId}&actor={players[0].PlayerId}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            var body = JsonSerializer.Deserialize<JsonElement>(text);
            Assert.Equal("not_found", body.GetProperty("code").GetString());
            Assert.Equal("Challenge not found.", body.GetProperty("title").GetString());
            Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
            Assert.DoesNotContain(view.ChallengeId.Value.ToString(), text);
            Assert.DoesNotContain(players[0].PlayerId.ToString(), text);
            Assert.DoesNotContain("participants", text);
        }
    }

    [Fact]
    public async Task Database_outage_returns_safe_503_without_resource_disclosure()
    {
        var caller = await factory.RegisterNewPlayerAsync("ChallengeOutage");
        await using var unreachable = new DatabaseOutageTests.UnreachableDatabaseApiFactory();
        using var client = unreachable.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", caller.AccessToken);
        using var response = await client.GetAsync(Path(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal("service_unavailable", JsonSerializer.Deserialize<JsonElement>(text).GetProperty("code").GetString());
        Assert.DoesNotContain("127.0.0.1", text);
        Assert.DoesNotContain("Npgsql", text);
    }

    [Fact]
    public async Task Openapi_documents_the_projection_uuid_roster_named_statuses_and_errors()
    {
        using var client = factory.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        var operation = document.GetProperty("paths").GetProperty("/api/v2/games/blood-money/challenges/{challengeId}").GetProperty("get");
        var parameter = Assert.Single(operation.GetProperty("parameters").EnumerateArray());
        Assert.Equal("challengeId", parameter.GetProperty("name").GetString());
        Assert.True(parameter.GetProperty("required").GetBoolean());
        Assert.Equal("uuid", parameter.GetProperty("schema").GetProperty("format").GetString());
        var responses = operation.GetProperty("responses");
        foreach (var status in new[] { "200", "401", "404", "503", "500" }) Assert.True(responses.TryGetProperty(status, out _));
        Assert.False(responses.GetProperty("401").TryGetProperty("content", out _));
        Assert.Equal("#/components/schemas/BloodMoneyChallengeDto", responses.GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        var schemas = document.GetProperty("components").GetProperty("schemas");
        var challenge = schemas.GetProperty("BloodMoneyChallengeDto").GetProperty("properties");
        Assert.Equal("uuid", challenge.GetProperty("challengeId").GetProperty("format").GetString());
        Assert.Equal("uuid", challenge.GetProperty("creatorPlayerId").GetProperty("format").GetString());
        Assert.Equal("int64", challenge.GetProperty("revision").GetProperty("format").GetString());
        Assert.Equal("int64", challenge.GetProperty("stakePerParticipant").GetProperty("format").GetString());
        Assert.Equal("#/components/schemas/BloodMoneyChallengeStatus", challenge.GetProperty("status").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/BloodMoneyChallengeParticipantDto", challenge.GetProperty("participants").GetProperty("items").GetProperty("$ref").GetString());
        AssertStringEnum(schemas.GetProperty("BloodMoneyChallengeStatus"), ["PendingAcceptance", "Active", "Declined", "Cancelled", "Expired"]);
        AssertStringEnum(schemas.GetProperty("BloodMoneyParticipantStatus"), ["Invited", "Accepted", "Declined"]);
        var participant = schemas.GetProperty("BloodMoneyChallengeParticipantDto").GetProperty("properties");
        Assert.Equal("uuid", participant.GetProperty("playerId").GetProperty("format").GetString());
        Assert.Equal("int32", participant.GetProperty("seatIndex").GetProperty("format").GetString());
        Assert.Equal("#/components/schemas/BloodMoneyParticipantStatus", participant.GetProperty("status").GetProperty("$ref").GetString());
        Assert.True(participant.GetProperty("acceptedAt").GetProperty("nullable").GetBoolean());
        foreach (var timestamp in new[] { "activatedAt", "gameplayDeadlineAt", "terminalAt" })
            Assert.True(challenge.GetProperty(timestamp).GetProperty("nullable").GetBoolean());
    }

    private static void AssertStringEnum(JsonElement schema, string[] names)
    {
        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal(names, schema.GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
    }

    private static void AssertTimestamp(DateTimeOffset? expected, JsonElement actual)
    {
        if (expected.HasValue) Assert.Equal(expected.Value, actual.GetDateTimeOffset());
        else Assert.Equal(JsonValueKind.Null, actual.ValueKind);
    }

    private static void AssertRoster(BloodMoneyChallengeView view, JsonElement body)
    {
        var roster = body.GetProperty("participants").EnumerateArray().ToArray();
        Assert.Equal(view.Participants.Count, roster.Length);
        for (var seat = 0; seat < roster.Length; seat++)
        {
            var expected = view.Participants[seat]; var participant = roster[seat];
            Assert.Equal(new[] { "acceptedAt", "playerId", "seatIndex", "status" }, participant.EnumerateObject().Select(property => property.Name).Order());
            Assert.Equal(expected.PlayerId.Value, participant.GetProperty("playerId").GetGuid());
            Assert.Equal(expected.SeatIndex, participant.GetProperty("seatIndex").GetInt32());
            Assert.Equal(expected.Status.ToString(), participant.GetProperty("status").GetString());
            AssertTimestamp(expected.AcceptedAt, participant.GetProperty("acceptedAt"));
        }
    }

    private async Task<(BloodMoneyChallengeView View, RegisteredPlayer[] Players)> Seed(int count,
        BloodMoneyChallengeStatus status = BloodMoneyChallengeStatus.PendingAcceptance, DateTimeOffset? createdAt = null)
    {
        var players = new List<RegisteredPlayer>();
        for (var i = 0; i < count; i++) players.Add(await factory.RegisterNewPlayerAsync("ChallengePlayer"));
        await using var scope = factory.Services.CreateAsyncScope(); var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<Level5V2DbContext>();
        var instant = createdAt ?? provider.GetRequiredService<IClock>().UtcNow;
        // PostgreSQL timestamps retain microseconds; keep fixture expectations exactly representable.
        var clock = new TestClock(instant.AddTicks(-(instant.Ticks % 10)));
        foreach (var player in players)
            await provider.GetRequiredService<IssueBloodCreditsUseCase>().ExecuteAsync(new(BloodCreditTransactionId.New(), new(player.PlayerId), 100, "challenge-api-fixture"), Ct);
        foreach (var player in players.Skip(1))
            await new FriendshipStore(db).AddFriendshipAsync(Friendship.Between(new(players[0].PlayerId), new(player.PlayerId), clock.UtcNow), Ct);
        await db.SaveChangesAsync();
        var reservations = new BloodCreditReservationStore(db);
        var financial = new BloodCreditReservationMutator(new BloodCreditLedgerStore(db), reservations, clock);
        var store = new BloodMoneyChallengeStore(db); var uow = new EfUnitOfWork(db);
        var view = await new CreateBloodMoneyChallengeUseCase(store, new FriendshipStore(db), financial, uow, clock, Timing)
            .ExecuteAsync(new(new(players[0].PlayerId), players.Skip(1).Select(player => new PlayerId(player.PlayerId)).ToArray(),
                10, "classic", 1, Guid.NewGuid()), Ct);
        var lifecycle = new BloodMoneyChallengeLifecycle(store, reservations, financial, uow, clock, Timing);
        view = status switch
        {
            BloodMoneyChallengeStatus.Active => await new AcceptBloodMoneyChallengeUseCase(lifecycle).ExecuteAsync(view.ChallengeId, new(players[1].PlayerId), Ct),
            BloodMoneyChallengeStatus.Declined => await new DeclineBloodMoneyChallengeUseCase(lifecycle).ExecuteAsync(view.ChallengeId, new(players[1].PlayerId), Ct),
            BloodMoneyChallengeStatus.Cancelled => await new CancelBloodMoneyChallengeUseCase(lifecycle).ExecuteAsync(view.ChallengeId, new(players[0].PlayerId), Ct),
            BloodMoneyChallengeStatus.Expired => await Expire(),
            _ => view
        };
        return (view, players.ToArray());

        async Task<BloodMoneyChallengeView> Expire()
        {
            clock.UtcNow = view.AcceptanceDeadlineAt;
            return await new ExpirePendingBloodMoneyChallengeUseCase(lifecycle).ExecuteAsync(view.ChallengeId, Ct);
        }
    }

    private async Task<string[]> Snapshot()
    {
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        var results = new List<string>();
        foreach (var table in new[] { "blood_money_challenges", "blood_money_challenge_participants", "blood_money_credit_accounts",
            "blood_money_credit_transactions", "blood_money_credit_postings", "blood_money_credit_reservations" })
        {
            var sql = $"SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text AS \"Value\" FROM {table} t";
            results.Add(await db.Database.SqlQueryRaw<string>(sql).SingleAsync());
        }
        return results.ToArray();
    }

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
