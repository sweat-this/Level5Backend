using System.Net;
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
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Level5.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class BloodMoneyChatFlowTests(ApiFactory factory)
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly BloodMoneyChallengeTimingPolicy Timing = new(TimeSpan.FromHours(1), TimeSpan.FromHours(2));
    private static string Path(Guid id) => $"/api/v2/games/blood-money/challenges/{id}";

    [Theory]
    [InlineData(2)][InlineData(3)][InlineData(4)]
    public async Task Chat_notifications_use_the_existing_private_inbox_and_replay_and_read_pointers_remain_independent(int count)
    {
        const string inbox = "/api/v2/platform/me/notifications";
        var (id, players) = await Seed(count);
        var key = Guid.NewGuid();
        var request = new { clientMessageId = key, body = "private body with stake 10 and balance 100" };
        Assert.Equal(HttpStatusCode.Created, (await players[0].Client.PostAsJsonAsync(Path(id) + "/messages", request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await players[0].Client.PostAsJsonAsync(Path(id) + "/messages", request)).StatusCode);
        Assert.Empty((await Json(await players[0].Client.GetAsync(inbox))).GetProperty("items").EnumerateArray());
        foreach (var recipient in players.Skip(1))
        {
            var item = Assert.Single((await Json(await recipient.Client.GetAsync(inbox))).GetProperty("items").EnumerateArray());
            Assert.Equal(new[] { "actionPath", "body", "createdAt", "id", "kind", "readAt", "source", "title" },
                item.EnumerateObject().Select(property => property.Name).Order());
            Assert.Equal("blood-money", item.GetProperty("source").GetString());
            Assert.Equal("challenge-chat", item.GetProperty("kind").GetString());
            Assert.Equal("New challenge chat activity", item.GetProperty("title").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("body").ValueKind);
            Assert.Equal("/account/games/blood-money", item.GetProperty("actionPath").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("readAt").ValueKind);
            var notificationPath = inbox + "/" + item.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.NotFound, (await players[0].Client.PatchAsJsonAsync(notificationPath, new { isRead = true })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await recipient.Client.PatchAsJsonAsync(notificationPath, new { isRead = true })).StatusCode);
            Assert.Equal(0, (await Page(recipient.Client, id)).GetProperty("lastReadSequence").GetInt64());
            Assert.Equal(HttpStatusCode.OK, (await recipient.Client.PutAsJsonAsync(Path(id) + "/read-position", new { lastReadSequence = 1 })).StatusCode);
            Assert.NotEqual(JsonValueKind.Null, Assert.Single((await Json(await recipient.Client.GetAsync(inbox)))
                .GetProperty("items").EnumerateArray()).GetProperty("readAt").ValueKind);
        }
    }

    [Fact]
    public async Task Muted_and_inactive_recipients_are_skipped_without_affecting_chat_or_financial_authority()
    {
        var (id, players) = await Seed(4);
        var before = await Snapshot(id);
        Assert.Equal(HttpStatusCode.OK, (await players[1].Client.PutAsJsonAsync(Path(id) + "/notifications-muted", new { notificationsMuted = true })).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
            var accountId = await db.PlayerProfiles.Where(row => row.Id == players[2].PlayerId).Select(row => row.AccountId).SingleAsync();
            await db.Accounts.Where(row => row.Id == accountId).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, "Disabled"));
        }
        Assert.Equal(HttpStatusCode.Created, (await players[0].Client.PostAsJsonAsync(Path(id) + "/messages",
            new { clientMessageId = Guid.NewGuid(), body = "hello" })).StatusCode);
        Assert.Equal(1, (await Page(players[1].Client, id)).GetProperty("items").GetArrayLength());
        await using var checkScope = factory.Services.CreateAsyncScope();
        var check = checkScope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        var playerIds = players.Select(player => player.PlayerId).ToArray();
        var recipients = await check.PlayerNotifications.Where(row => row.Source == "blood-money" &&
            playerIds.Contains(row.RecipientPlayerId)).Select(row => row.RecipientPlayerId).ToListAsync();
        Assert.Equal(players[3].PlayerId, Assert.Single(recipients));
        Assert.Equal(before, await Snapshot(id));
    }

    [Fact]
    public async Task Eligibility_persistence_outage_fails_closed_with_safe_503_on_every_operation()
    {
        var caller = await factory.RegisterNewPlayerAsync("ChatOutage");
        await using var unreachable = new DatabaseOutageTests.UnreachableDatabaseApiFactory();
        using var client = unreachable.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", caller.AccessToken);
        var responses = await Task.WhenAll(new[] { ("GET", "messages"), ("POST", "messages"), ("PUT", "read-position"),
            ("PUT", "notifications-muted"), ("POST", "message-reports") }.Select(op => Request(client, Guid.NewGuid(), op.Item1, op.Item2)));
        foreach (var response in responses)
        {
            await Error(response, HttpStatusCode.ServiceUnavailable, "service_unavailable");
            Assert.DoesNotContain("127.0.0.1", await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData("GET", "messages")][InlineData("POST", "messages")]
    [InlineData("PUT", "read-position")][InlineData("PUT", "notifications-muted")][InlineData("POST", "message-reports")]
    public async Task Every_operation_requires_bearer_and_rechecks_current_disabled_account_before_any_disclosure(string method, string operation)
    {
        var (id, players) = await Seed(); var caller = players[0];
        Assert.Equal(HttpStatusCode.Unauthorized, (await Request(factory.CreateClient(), id, method, operation)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        var privateId = await db.Set<PlayerProfileRow>().Where(row => row.Id == caller.PlayerId).Select(row => row.AccountId).SingleAsync();
        await db.Set<AccountRow>().Where(row => row.Id == privateId).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, "Disabled"));
        foreach (var target in new[] { id, Guid.NewGuid() })
        {
            var response = await Request(caller.Client, target, method, operation, malformed: true);
            await Error(response, HttpStatusCode.Forbidden, "chat_communication_restricted");
            Assert.DoesNotContain(privateId.ToString(), await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData("GET", "messages")][InlineData("POST", "messages")]
    [InlineData("PUT", "read-position")][InlineData("PUT", "notifications-muted")][InlineData("POST", "message-reports")]
    public async Task Unknown_nonparticipant_and_never_activated_resources_share_safe_404(string method, string operation)
    {
        var (id, players) = await Seed(); var outsider = await factory.RegisterNewPlayerAsync("ChatOutsider");
        var pending = await Seed(active: false);
        foreach (var entry in new[] { (Guid.NewGuid(), players[0].Client), (id, outsider.Client), (pending.Id, pending.Players[0].Client) })
            await Error(await Request(entry.Item2, entry.Item1, method, operation), HttpStatusCode.NotFound, "not_found");
        if (method == "GET")
            await Error(await outsider.Client.GetAsync(Path(id) + "/messages?cursor=corrupted"), HttpStatusCode.NotFound, "not_found");
    }

    [Theory]
    [InlineData(2)][InlineData(3)][InlineData(4)]
    public async Task Active_participants_send_normalized_messages_and_recover_replays_without_financial_mutation(int count)
    {
        var (id, players) = await Seed(count); var before = await Snapshot(id);
        foreach (var caller in players)
        {
            var key = Guid.NewGuid();
            var response = await caller.Client.PostAsJsonAsync(Path(id) + "/messages", new
            { clientMessageId = key, body = " e\u0301\r\nhello ", senderPlayerId = players[^1].PlayerId });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var message = await Json(response);
            Assert.Equal(caller.PlayerId, message.GetProperty("senderPlayerId").GetGuid());
            Assert.Equal("é\nhello", message.GetProperty("body").GetString());
            Assert.Equal(8, message.EnumerateObject().Count());
            Assert.Equal("Visible", message.GetProperty("visibility").GetString());
            var replay = await caller.Client.PostAsJsonAsync(Path(id) + "/messages", new { clientMessageId = key, body = "é\nhello" });
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal(message.ToString(), (await Json(replay)).ToString());
            await Error(await caller.Client.PostAsJsonAsync(Path(id) + "/messages", new { clientMessageId = key, body = "changed" }),
                HttpStatusCode.Conflict, "chat_message_conflict");
        }
        var page = await Page(players[0].Client, id);
        Assert.Equal(count, page.GetProperty("items").GetArrayLength()); Assert.Equal(count, page.GetProperty("latestSequence").GetInt64());
        Assert.Equal(before, await Snapshot(id));
    }

    [Fact]
    public async Task Accepted_rate_budget_returns_retry_after_while_exact_retry_still_recovers_original()
    {
        var (id, players) = await Seed(); var client = players[0].Client; var key = Guid.NewGuid();
        var first = await client.PostAsJsonAsync(Path(id) + "/messages", new { clientMessageId = key, body = "rate" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        for (var i = 1; i < 5; i++)
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Path(id) + "/messages", new { clientMessageId = Guid.NewGuid(), body = "rate" })).StatusCode);
        var rejected = await client.PostAsJsonAsync(Path(id) + "/messages", new { clientMessageId = Guid.NewGuid(), body = "rate" });
        await Error(rejected, HttpStatusCode.TooManyRequests, "chat_rate_limited");
        Assert.InRange(rejected.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 10);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Path(id) + "/messages", new { clientMessageId = key, body = "rate" })).StatusCode);
        Assert.Equal(5, (await Page(client, id)).GetProperty("latestSequence").GetInt64());
    }

    [Theory]
    [InlineData("")][InlineData("\t")][InlineData("\u0000")]
    public async Task Invalid_text_is_a_safe_stable_400(string body)
    {
        var (id, players) = await Seed();
        await Error(await players[0].Client.PostAsJsonAsync(Path(id) + "/messages", new { clientMessageId = Guid.NewGuid(), body }),
            HttpStatusCode.BadRequest, "invalid_chat_message");
        using var raw = new StringContent("{\"clientMessageId\":\"" + Guid.NewGuid() + "\",\"body\":\"\\uD800\"}", System.Text.Encoding.UTF8, "application/json");
        await Error(await players[0].Client.PostAsync(Path(id) + "/messages", raw), HttpStatusCode.BadRequest, "invalid_chat_message");
    }

    [Theory]
    [InlineData("limit=0", "invalid_chat_limit")][InlineData("limit=101", "invalid_chat_limit")]
    [InlineData("limit=abc", "invalid_chat_limit")][InlineData("limit=", "invalid_chat_limit")]
    [InlineData("cursor=bad", "invalid_chat_cursor")][InlineData("cursor=", "invalid_chat_cursor")]
    [InlineData("cursor=a&cursor=b", "invalid_chat_cursor")]
    public async Task Invalid_list_inputs_have_contract_codes(string query, string code)
    {
        var (id, players) = await Seed();
        await Error(await players[0].Client.GetAsync(Path(id) + "/messages?" + query), HttpStatusCode.BadRequest, code);
    }

    [Fact]
    public async Task Read_and_mute_are_caller_private_monotonic_and_preserve_each_other_under_concurrency()
    {
        var (id, players) = await Seed(); var client = players[0].Client;
        for (var i = 0; i < 3; i++) await client.PostAsJsonAsync(Path(id) + "/messages", new { clientMessageId = Guid.NewGuid(), body = "read" });
        Assert.Equal(0, (await Json(await client.PutAsJsonAsync(Path(id) + "/read-position", new { lastReadSequence = 0 }))).GetProperty("lastReadSequence").GetInt64());
        var results = await Task.WhenAll(
            client.PutAsJsonAsync(Path(id) + "/read-position", new { lastReadSequence = 3, playerId = players[1].PlayerId }),
            client.PutAsJsonAsync(Path(id) + "/read-position", new { lastReadSequence = 1 }),
            client.PutAsJsonAsync(Path(id) + "/notifications-muted", new { notificationsMuted = true }));
        Assert.All(results, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var stale = await Json(await client.PutAsJsonAsync(Path(id) + "/read-position", new { lastReadSequence = 1 }));
        Assert.Equal(3, stale.GetProperty("lastReadSequence").GetInt64()); Assert.Single(stale.EnumerateObject());
        await Error(await client.PutAsJsonAsync(Path(id) + "/read-position", new { lastReadSequence = 4 }), HttpStatusCode.BadRequest, "invalid_chat_read_position");
        var own = await Page(client, id); var other = await Page(players[1].Client, id);
        Assert.Equal(3, own.GetProperty("lastReadSequence").GetInt64()); Assert.True(own.GetProperty("notificationsMuted").GetBoolean());
        Assert.Equal(0, other.GetProperty("lastReadSequence").GetInt64()); Assert.False(other.GetProperty("notificationsMuted").GetBoolean());
        foreach (var muted in new[] { true, true, false })
        {
            var result = await Json(await client.PutAsJsonAsync(Path(id) + "/notifications-muted", new { notificationsMuted = muted }));
            Assert.Single(result.EnumerateObject()); Assert.Equal(muted, result.GetProperty("notificationsMuted").GetBoolean());
        }
    }

    [Fact]
    public async Task Reports_accept_visible_and_suppressed_targets_without_leaking_evidence_or_identity()
    {
        var (id, players) = await Seed(); var key = Guid.NewGuid(); const string evidence = "protected-chat-evidence-secret";
        var message = await Json(await players[0].Client.PostAsJsonAsync(Path(id) + "/messages", new { clientMessageId = key, body = evidence }));
        var messageId = message.GetProperty("messageId").GetGuid();
        var report = await players[1].Client.PostAsJsonAsync(Path(id) + "/message-reports", new { messageId, reason = "Hate", reporterPlayerId = players[0].PlayerId });
        Assert.Equal(HttpStatusCode.Created, report.StatusCode);
        var acceptance = await Json(report); Assert.Equal("reportId", Assert.Single(acceptance.EnumerateObject()).Name);
        await Error(await players[1].Client.PostAsJsonAsync(Path(id) + "/message-reports", new { messageId, reason = "Other" }), HttpStatusCode.Conflict, "chat_report_already_exists");
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        await db.Set<BloodMoneyChatMessageRow>().Where(row => row.Id == messageId).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Visibility, "Suppressed"));
        var second = await players[0].Client.PostAsJsonAsync(Path(id) + "/message-reports", new { messageId, reason = "Spam" });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var pageResponse = await players[0].Client.GetAsync(Path(id) + "/messages");
        Assert.True(pageResponse.Headers.CacheControl!.NoStore);
        var page = await Json(pageResponse); var tombstone = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, tombstone.GetProperty("body").ValueKind); Assert.Equal("Suppressed", tombstone.GetProperty("visibility").GetString());
        var replay = await players[0].Client.PostAsJsonAsync(Path(id) + "/messages", new { clientMessageId = key, body = evidence });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        foreach (var response in new[] { report, second, pageResponse, replay })
        {
            var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain(evidence, text);
            Assert.DoesNotContain("accountId", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("email", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reservation", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("stackTrace", text, StringComparison.OrdinalIgnoreCase);
        }
        var durable = await db.Set<BloodMoneyChatReportRow>().AsNoTracking().Where(row => row.ChallengeId == id).ToListAsync();
        Assert.Equal("Hate", durable.Single(row => row.ReporterPlayerId == players[1].PlayerId).Reason);
        Assert.Equal(evidence, (await db.Set<BloodMoneyChatMessageRow>().AsNoTracking().SingleAsync(row => row.Id == messageId)).Body);
    }

    [Theory]
    [InlineData("Unknown")][InlineData("0")][InlineData("")][InlineData("harassment")]
    public async Task Unknown_report_reason_is_rejected_after_resource_authorization(string reason)
    {
        var (id, players) = await Seed(); var messageId = Guid.NewGuid();
        await Error(await players[0].Client.PostAsJsonAsync(Path(id) + "/message-reports", new { messageId, reason }), HttpStatusCode.BadRequest, "invalid_chat_report");
        await Error(await players[0].Client.PostAsJsonAsync(Path(Guid.NewGuid()) + "/message-reports", new { messageId, reason }), HttpStatusCode.NotFound, "not_found");
        await Error(await players[0].Client.PostAsJsonAsync(Path(id) + "/message-reports", new { messageId, reason = 100 }), HttpStatusCode.BadRequest, "invalid_chat_report");
    }

    [Fact]
    public async Task Foreign_and_missing_report_targets_share_safe_404()
    {
        var (id, players) = await Seed(); var foreign = await Seed();
        var message = await Json(await foreign.Players[0].Client.PostAsJsonAsync(Path(foreign.Id) + "/messages", new { clientMessageId = Guid.NewGuid(), body = "foreign" }));
        foreach (var messageId in new[] { Guid.NewGuid(), message.GetProperty("messageId").GetGuid() })
            await Error(await players[0].Client.PostAsJsonAsync(Path(id) + "/message-reports", new { messageId, reason = "Other" }), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Http_older_and_resume_cursors_preserve_history_and_backlog_boundaries()
    {
        var (id, players) = await Seed(); var client = players[0].Client;
        var empty = await Page(client, id);
        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Path(id) + "/messages", new { clientMessageId = Guid.NewGuid(), body = $"page-{i}" })).StatusCode);
        var initial = await Json(await client.GetAsync(Path(id) + "/messages?limit=2"));
        Assert.Equal(new long[] { 3, 4 }, Sequences(initial));
        var olderToken = initial.GetProperty("olderCursor").GetString();
        await client.PostAsJsonAsync(Path(id) + "/messages", new { clientMessageId = Guid.NewGuid(), body = "new insert" });
        var older = await Json(await client.GetAsync(Path(id) + "/messages?limit=2&cursor=" + olderToken));
        Assert.Equal(new long[] { 1, 2 }, Sequences(older)); Assert.Equal(JsonValueKind.Null, older.GetProperty("olderCursor").ValueKind);
        var resume = empty.GetProperty("resumeCursor").GetString(); var collected = new List<long>();
        foreach (var expected in new[] { 2, 2, 1, 0 })
        {
            var batch = await Json(await client.GetAsync(Path(id) + "/messages?limit=2&cursor=" + resume));
            var sequences = Sequences(batch); Assert.Equal(expected, sequences.Length); collected.AddRange(sequences);
            var next = batch.GetProperty("resumeCursor").GetString(); if (expected == 0) Assert.Equal(resume, next); resume = next;
        }
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, collected);
        var foreign = await Seed();
        await Error(await foreign.Players[0].Client.GetAsync(Path(foreign.Id) + "/messages?cursor=" + olderToken), HttpStatusCode.BadRequest, "invalid_chat_cursor");
        await Error(await client.GetAsync(Path(id) + "/messages?cursor=" + new string('A', 1025)), HttpStatusCode.BadRequest, "invalid_chat_cursor");
        await Error(await players[1].Client.GetAsync(Path(id) + "/messages?cursor=" + olderToken![..^1] + (olderToken[^1] == 'A' ? "B" : "A")), HttpStatusCode.BadRequest, "invalid_chat_cursor");
    }

    private static long[] Sequences(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("sequence").GetInt64()).ToArray();

    [Fact]
    public async Task Openapi_publishes_five_operations_bounded_inputs_tombstones_and_int64_positions()
    {
        var document = await Json(await factory.CreateClient().GetAsync("/swagger/v1/swagger.json"));
        var paths = document.GetProperty("paths").EnumerateObject().Where(p => p.Name.StartsWith("/api/v2/games/blood-money/challenges/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(5, paths.Sum(p => p.Value.EnumerateObject().Count()));
        const string root = "/api/v2/games/blood-money/challenges/{challengeId}";
        foreach (var (route, method, status, schema) in new[]
        {
            ("messages", "get", "200", "BloodMoneyChatPageDto"),
            ("messages", "post", "201", "BloodMoneyChatMessageDto"),
            ("messages", "post", "200", "BloodMoneyChatMessageDto"),
            ("read-position", "put", "200", "BloodMoneyChatReadPositionDto"),
            ("notifications-muted", "put", "200", "BloodMoneyChatNotificationsMutedDto"),
            ("message-reports", "post", "201", "BloodMoneyChatReportAcceptanceDto")
        })
        {
            var responses = document.GetProperty("paths").GetProperty($"{root}/{route}").GetProperty(method).GetProperty("responses");
            Assert.True(responses.TryGetProperty(status, out var response), $"{method.ToUpperInvariant()} {route} must document {status}.");
            Assert.Equal($"#/components/schemas/{schema}", response.GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        }
        var schemas = document.GetProperty("components").GetProperty("schemas");
        var page = schemas.GetProperty("BloodMoneyChatPageDto").GetProperty("properties");
        Assert.Equal(7, page.EnumerateObject().Count());
        Assert.Equal("#/components/schemas/BloodMoneyChatMessageDto", page.GetProperty("items").GetProperty("items").GetProperty("$ref").GetString());
        Assert.True(page.GetProperty("olderCursor").GetProperty("nullable").GetBoolean());
        Assert.Equal("int64", page.GetProperty("latestSequence").GetProperty("format").GetString());
        Assert.Equal("int64", page.GetProperty("lastReadSequence").GetProperty("format").GetString());
        var message = schemas.GetProperty("BloodMoneyChatMessageDto").GetProperty("properties");
        Assert.Equal(8, message.EnumerateObject().Count());
        Assert.True(message.GetProperty("body").GetProperty("nullable").GetBoolean());
        Assert.Equal("int64", message.GetProperty("sequence").GetProperty("format").GetString());
        Assert.Equal("int64", schemas.GetProperty("BloodMoneyChatReadPositionDto").GetProperty("properties").GetProperty("lastReadSequence").GetProperty("format").GetString());
        Assert.Equal(6, schemas.GetProperty("BloodMoneyChatReportReason").GetProperty("enum").GetArrayLength());
        var send = schemas.GetProperty("SendBloodMoneyChatMessageDto").GetProperty("properties");
        Assert.Equal(new[] { "body", "clientMessageId" }, send.EnumerateObject().Select(p => p.Name).Order());
    }

    private async Task<(Guid Id, RegisteredPlayer[] Players)> Seed(int count = 2, bool active = true)
    {
        var players = new List<RegisteredPlayer>();
        for (var i = 0; i < count; i++) players.Add(await factory.RegisterNewPlayerAsync("ChatPlayer"));
        await using var scope = factory.Services.CreateAsyncScope(); var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<Level5V2DbContext>(); var clock = provider.GetRequiredService<IClock>();
        foreach (var player in players)
            await provider.GetRequiredService<IssueBloodCreditsUseCase>().ExecuteAsync(new(BloodCreditTransactionId.New(), new(player.PlayerId), 100, "chat-api-fixture"), Ct);
        foreach (var player in players.Skip(1))
            await new FriendshipStore(db).AddFriendshipAsync(Friendship.Between(new(players[0].PlayerId), new(player.PlayerId), clock.UtcNow), Ct);
        await db.SaveChangesAsync();
        var ledger = new BloodCreditLedgerStore(db); var reservations = new BloodCreditReservationStore(db);
        var financial = new BloodCreditReservationMutator(ledger, reservations, clock); var uow = new EfUnitOfWork(db);
        var store = new BloodMoneyChallengeStore(db);
        var challenge = await new CreateBloodMoneyChallengeUseCase(store, new FriendshipStore(db), financial, uow, clock, Timing)
            .ExecuteAsync(new(new(players[0].PlayerId), players.Skip(1).Select(p => new PlayerId(p.PlayerId)).ToArray(), 10, "classic", 1, Guid.NewGuid()), Ct);
        if (active)
            foreach (var player in players.Skip(1))
                await new AcceptBloodMoneyChallengeUseCase(new(store, reservations, financial, uow, clock, Timing)).ExecuteAsync(challenge.ChallengeId, new(player.PlayerId), Ct);
        return (challenge.ChallengeId.Value, players.ToArray());
    }

    private async Task<string[]> Snapshot(Guid challengeId)
    {
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        var results = new List<string>();
        foreach (var table in new[] { "blood_money_challenges", "blood_money_challenge_participants", "blood_money_credit_accounts",
            "blood_money_credit_transactions", "blood_money_credit_postings", "blood_money_credit_reservations" })
        {
            // Table names come exclusively from the fixed fixture roster above.
            var sql = $"SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text AS \"Value\" FROM {table} t";
            results.Add(await db.Database.SqlQueryRaw<string>(sql).SingleAsync());
        }
        return results.ToArray();
    }
    private static async Task<HttpResponseMessage> Request(HttpClient client, Guid id, string method, string operation, bool malformed = false)
    {
        object body = operation switch
        {
            "messages" => new { clientMessageId = Guid.NewGuid(), body = "hello" },
            "read-position" => new { lastReadSequence = 0 },
            "notifications-muted" => new { notificationsMuted = true },
            _ => new { messageId = Guid.NewGuid(), reason = "Other" }
        };
        using var request = new HttpRequestMessage(new HttpMethod(method), Path(id) + "/" + operation)
        { Content = malformed ? new StringContent("{", System.Text.Encoding.UTF8, "application/json") : JsonContent.Create(body) };
        return await client.SendAsync(request);
    }
    private static async Task<JsonElement> Page(HttpClient client, Guid id) => await Json(await client.GetAsync(Path(id) + "/messages"));
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    { response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync<JsonElement>(); }
    private static async Task Error(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode); var raw = await response.Content.ReadAsStringAsync();
        var json = JsonSerializer.Deserialize<JsonElement>(raw); Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.DoesNotContain("Npgsql", raw); Assert.DoesNotContain("stackTrace", raw, StringComparison.OrdinalIgnoreCase);
    }
}
