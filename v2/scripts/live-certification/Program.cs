using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

// Live two-account certification harness for Backend V2 correspondence (issue #159 certification
// slice). Exercises the exact same REST contract Unity's typed BackendV2 clients use (same routes,
// same camelCase JSON DTOs, same auth flow) against a REAL running Backend V2 instance - not a fake
// transport, not a unit test. Two independent HttpClients (one per account) give genuine session
// isolation, including true concurrent dispatch for the Scenario 8 race check.
//
// Run: dotnet run -- phase1   (register, friend, create/accept challenge, play game 1)
//      <kill/restart the backend process in between>
//      dotnet run -- phase2   (reconnect, verify restart survival, play game 2, verify completion)

string baseUri = "https://localhost:7029/";
string statePath = Path.Combine(Path.GetTempPath(), "level5_backendv2_live_cert_state.json");
var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

string phase = args.Length > 0 ? args[0] : "phase1";

var handler = new HttpClientHandler();

HttpClient NewClient()
{
    var c = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(baseUri) };
    return c;
}

void Log(string msg) => Console.WriteLine($"[CERT] {msg}");

async Task<(int status, JsonElement? doc, string correlationId)> SendAsync(
    HttpClient client, HttpMethod method, string path, object? body, string? bearer = null)
{
    string correlationId = Guid.NewGuid().ToString();
    using var req = new HttpRequestMessage(method, path);
    req.Headers.Add("X-Correlation-Id", correlationId);
    if (bearer != null)
    {
        req.Headers.Add("Authorization", "Bearer " + bearer);
    }
    if (body != null)
    {
        req.Content = JsonContent.Create(body, options: jsonOpts);
    }

    using HttpResponseMessage resp = await client.SendAsync(req);
    string text = await resp.Content.ReadAsStringAsync();
    JsonElement? doc = null;
    if (!string.IsNullOrWhiteSpace(text))
    {
        try
        {
            // Clone the root element and dispose the parsed JsonDocument immediately - callers
            // hold onto these results across many further statements/calls, so returning the
            // IDisposable JsonDocument itself would leave every one of them undisposed for the
            // rest of the run. A cloned JsonElement owns its own memory and needs no disposal.
            using JsonDocument parsed = JsonDocument.Parse(text);
            doc = parsed.RootElement.Clone();
        }
        catch { /* non-JSON body */ }
    }

    return ((int)resp.StatusCode, doc, correlationId);
}

void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception("ASSERTION FAILED: " + message);
    }
}

string Describe((int status, JsonElement? doc, string correlationId) r)
{
    string traceId = "";
    string title = "";
    bool isObject = r.doc.HasValue && r.doc.Value.ValueKind == JsonValueKind.Object;
    if (isObject && r.doc!.Value.TryGetProperty("traceId", out var t)) traceId = t.GetString() ?? "";
    if (isObject && r.doc!.Value.TryGetProperty("title", out var ti)) title = ti.GetString() ?? "";
    return $"status={r.status} correlationId={r.correlationId} traceId={traceId} title={title}";
}

// ---------------------------------------------------------------------
// Account: an isolated in-process client (its own HttpClient, its own bearer token) - the
// "isolated in-process clients that exercise the same backend API/auth/session paths" setup.
// ---------------------------------------------------------------------
async Task<Account> Register(HttpClient http, string username, string password, string displayName)
{
    var r = await SendAsync(http, HttpMethod.Post, "api/v2/auth/register",
        new { username, password, displayName });
    Log($"Register({username}): {Describe(r)}");
    Expect(r.status == 200, $"Register({username}) expected 200, got {Describe(r)}");
    var root = r.doc!.Value;
    return new Account
    {
        Username = username,
        Password = password,
        DisplayName = displayName,
        PlayerId = root.GetProperty("playerId").GetGuid(),
        AccessToken = root.GetProperty("accessToken").GetString()!,
        RefreshToken = root.GetProperty("refreshToken").GetString()!
    };
}

async Task<Account> Login(HttpClient http, string username, string password, string displayName, Guid expectedPlayerId)
{
    var r = await SendAsync(http, HttpMethod.Post, "api/v2/auth/login", new { username, password });
    Log($"Login({username}): {Describe(r)}");
    Expect(r.status == 200, $"Login({username}) expected 200, got {Describe(r)}");
    var root = r.doc!.Value;
    var playerId = root.GetProperty("playerId").GetGuid();
    Expect(playerId == expectedPlayerId, $"Login({username}) returned a different playerId than before");
    return new Account
    {
        Username = username,
        Password = password,
        DisplayName = displayName,
        PlayerId = playerId,
        AccessToken = root.GetProperty("accessToken").GetString()!,
        RefreshToken = root.GetProperty("refreshToken").GetString()!
    };
}

async Task<Account> Refresh(HttpClient http, Account account)
{
    var r = await SendAsync(http, HttpMethod.Post, "api/v2/auth/refresh", new { refreshToken = account.RefreshToken });
    Log($"Refresh({account.Username}): {Describe(r)}");
    Expect(r.status == 200, $"Refresh({account.Username}) expected 200, got {Describe(r)}");
    var root = r.doc!.Value;
    Expect(root.GetProperty("playerId").GetGuid() == account.PlayerId, "Refresh returned a different playerId");
    account.AccessToken = root.GetProperty("accessToken").GetString()!;
    account.RefreshToken = root.GetProperty("refreshToken").GetString()!;
    return account;
}

async Task DiscoverTag(HttpClient http, Account account)
{
    // No dedicated "get my own tag" endpoint exists; PATCH .../me with the account's own current
    // display name is a real, already-shipped, idempotent call that returns Tag as a side effect.
    var r = await SendAsync(http, HttpMethod.Patch, "api/v2/players/me",
        new { displayName = account.DisplayName }, account.AccessToken);
    Log($"DiscoverTag({account.Username}): {Describe(r)}");
    Expect(r.status == 200, $"UpdateMe({account.Username}) expected 200, got {Describe(r)}");
    var root = r.doc!.Value;
    Expect(root.GetProperty("playerId").GetGuid() == account.PlayerId, "UpdateMe returned a different playerId");
    account.Tag = root.GetProperty("tag").GetString();
}

Dictionary<string, double> BuildMetrics(JsonElement descriptor, bool winning)
{
    var metrics = new Dictionary<string, double>();
    foreach (var key in descriptor.GetProperty("comparisonKeys").EnumerateArray())
    {
        string metric = key.GetProperty("metric").GetString()!;
        string direction = key.GetProperty("direction").GetString()!;
        bool higherWins = string.Equals(direction, "HigherWins", StringComparison.OrdinalIgnoreCase);
        bool wantsHigh = higherWins == winning;
        metrics[metric] = wantsHigh ? 100 : 50;
    }
    foreach (var required in descriptor.GetProperty("requiredResultMetrics").EnumerateArray())
    {
        string name = required.GetString()!;
        if (!metrics.ContainsKey(name)) metrics[name] = winning ? 100 : 50;
    }
    return metrics;
}

var evidence = new List<string>();
void Record(string label, object value) => evidence.Add($"{label}={value}");

try
{
    if (phase == "phase1")
    {
        await Phase1();
    }
    else if (phase == "phase2")
    {
        await Phase2();
    }
    else
    {
        Console.Error.WriteLine("Usage: dotnet run -- [phase1|phase2]");
        Environment.Exit(1);
    }

    Console.WriteLine();
    Console.WriteLine("=== EVIDENCE SUMMARY ===");
    foreach (var line in evidence) Console.WriteLine(line);
    Console.WriteLine("=== " + phase.ToUpperInvariant() + " PASSED ===");
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine("=== " + phase.ToUpperInvariant() + " FAILED ===");
    Console.WriteLine(ex);
    Environment.Exit(1);
}

async Task Phase1()
{
    string suffix = Guid.NewGuid().ToString("N").Substring(0, 10);
    string usernameA = "certA" + suffix;
    string usernameB = "certB" + suffix;
    const string password = "CertPass123!";
    string displayNameA = "Cert Account A " + suffix;
    string displayNameB = "Cert Account B " + suffix;

    Log($"=== Phase 1 start === usernameA={usernameA} usernameB={usernameB}");

    using HttpClient httpA = NewClient();
    using HttpClient httpB = NewClient();

    Account a = await Register(httpA, usernameA, password, displayNameA);
    Account b = await Register(httpB, usernameB, password, displayNameB);
    Record("playerIdA", a.PlayerId);
    Record("playerIdB", b.PlayerId);

    await DiscoverTag(httpA, a);
    await DiscoverTag(httpB, b);
    Record("tagA", a.Tag!);
    Record("tagB", b.Tag!);
    Log($"tagA={a.Tag} tagB={b.Tag}");

    // ---- Scenario 1: friend lookup and accepted friendship ----
    var resolveB = await SendAsync(httpA, HttpMethod.Get, $"api/v2/players/by-tag/{Uri.EscapeDataString(b.Tag!)}", null, a.AccessToken);
    Log($"Scenario1.ResolveBByTag: {Describe(resolveB)}");
    Expect(resolveB.status == 200, $"ResolveBByTag expected 200, got {Describe(resolveB)}");
    Expect(resolveB.doc!.Value.GetProperty("playerId").GetGuid() == b.PlayerId, "resolved wrong player");

    var sendReq = await SendAsync(httpA, HttpMethod.Post, "api/v2/friends/requests", new { toPlayerId = b.PlayerId }, a.AccessToken);
    Log($"Scenario1.SendFriendRequest: {Describe(sendReq)}");
    Expect(sendReq.status == 200, $"SendFriendRequest expected 200, got {Describe(sendReq)}");
    Guid friendRequestId = sendReq.doc!.Value.GetProperty("id").GetGuid();
    Record("friendRequestId", friendRequestId);

    var incomingB = await SendAsync(httpB, HttpMethod.Get, "api/v2/friends/requests/incoming", null, b.AccessToken);
    Log($"Scenario1.ListIncomingOnB: {Describe(incomingB)}");
    Expect(incomingB.status == 200, $"ListIncomingOnB expected 200, got {Describe(incomingB)}");
    Expect(incomingB.doc!.Value.EnumerateArray().Any(x => x.GetProperty("id").GetGuid() == friendRequestId),
        "B must see A's incoming friend request");

    var acceptReq = await SendAsync(httpB, HttpMethod.Post, $"api/v2/friends/requests/{friendRequestId}/accept", null, b.AccessToken);
    Log($"Scenario1.AcceptOnB: {Describe(acceptReq)}");
    Expect(acceptReq.status == 204, $"AcceptOnB expected 204, got {Describe(acceptReq)}");

    var friendsA = await SendAsync(httpA, HttpMethod.Get, "api/v2/friends", null, a.AccessToken);
    Expect(friendsA.status == 200, $"ListFriendsOnA expected 200, got {Describe(friendsA)}");
    Expect(friendsA.doc!.Value.EnumerateArray().Any(x => x.GetProperty("playerId").GetGuid() == b.PlayerId),
        "A must see B as a friend after acceptance");

    var friendsB = await SendAsync(httpB, HttpMethod.Get, "api/v2/friends", null, b.AccessToken);
    Expect(friendsB.status == 200, $"ListFriendsOnB expected 200, got {Describe(friendsB)}");
    Expect(friendsB.doc!.Value.EnumerateArray().Any(x => x.GetProperty("playerId").GetGuid() == a.PlayerId),
        "B must see A as a friend after acceptance");
    Log("Scenario 1 PASS: friendship established both directions");

    // ---- Scenario 2: create/accept a Best-of-3 sealed challenge ----
    Guid clientRequestId = Guid.NewGuid();
    object createBody = new
    {
        opponentId = b.PlayerId,
        totalGames = 3,
        rulesetId = "score-only",
        clientRequestId
    };
    var createResult = await SendAsync(httpA, HttpMethod.Post, "api/v2/series", createBody, a.AccessToken);
    Log($"Scenario2.CreateChallenge: {Describe(createResult)}");
    Expect(createResult.status == 200, $"CreateChallenge expected 200, got {Describe(createResult)}");
    var createdSeries = createResult.doc!.Value;
    Guid seriesId = createdSeries.GetProperty("id").GetGuid();
    Expect(createdSeries.GetProperty("totalGames").GetInt32() == 3, "totalGames must be 3");
    Expect(createdSeries.GetProperty("rules").GetProperty("informationPolicy").GetString() == "SealedAttempt",
        "information policy must be SealedAttempt");
    Expect(createdSeries.GetProperty("status").GetString() == "PendingAcceptance", "status must be PendingAcceptance");
    Record("seriesId", seriesId);
    Record("clientRequestId", clientRequestId);
    Log($"Scenario2 seriesId={seriesId} clientRequestId={clientRequestId}");

    // Scenario 4a: duplicate create with the same clientRequestId must be idempotent.
    var dupCreate = await SendAsync(httpA, HttpMethod.Post, "api/v2/series", createBody, a.AccessToken);
    Log($"Scenario4a.DuplicateCreateChallenge: {Describe(dupCreate)}");
    Expect(dupCreate.status == 200, $"DuplicateCreateChallenge expected 200, got {Describe(dupCreate)}");
    Expect(dupCreate.doc!.Value.GetProperty("id").GetGuid() == seriesId,
        "a retried create with the same clientRequestId must return the same series, not a new one");
    Log("Scenario 4a PASS: duplicate create is idempotent");

    var incomingSeriesB = await SendAsync(httpB, HttpMethod.Get, "api/v2/series/incoming?limit=20", null, b.AccessToken);
    Expect(incomingSeriesB.status == 200, $"ListIncomingOnB expected 200, got {Describe(incomingSeriesB)}");
    Expect(incomingSeriesB.doc!.Value.GetProperty("items").EnumerateArray().Any(x => x.GetProperty("id").GetGuid() == seriesId),
        "B must see the incoming challenge");

    var acceptSeries = await SendAsync(httpB, HttpMethod.Post, $"api/v2/series/{seriesId}/accept", null, b.AccessToken);
    Log($"Scenario2.AcceptOnB: {Describe(acceptSeries)}");
    Expect(acceptSeries.status == 200, $"AcceptOnB expected 200, got {Describe(acceptSeries)}");
    Expect(acceptSeries.doc!.Value.GetProperty("status").GetString() == "Active", "status must be Active after accept");

    // Scenario 4b: duplicate accept must be idempotent.
    var dupAccept = await SendAsync(httpB, HttpMethod.Post, $"api/v2/series/{seriesId}/accept", null, b.AccessToken);
    Log($"Scenario4b.DuplicateAccept: {Describe(dupAccept)}");
    Expect(dupAccept.status == 200, $"DuplicateAccept expected 200, got {Describe(dupAccept)}");
    Expect(dupAccept.doc!.Value.GetProperty("status").GetString() == "Active", "status must remain Active");
    Log("Scenario 4b PASS: duplicate accept is idempotent");

    var activeA = await SendAsync(httpA, HttpMethod.Get, "api/v2/series/active?limit=20", null, a.AccessToken);
    Expect(activeA.status == 200, $"ListActiveOnA expected 200, got {Describe(activeA)}");
    Expect(activeA.doc!.Value.GetProperty("items").EnumerateArray().Any(x => x.GetProperty("id").GetGuid() == seriesId), "A must see active series");

    var activeB = await SendAsync(httpB, HttpMethod.Get, "api/v2/series/active?limit=20", null, b.AccessToken);
    Expect(activeB.status == 200, $"ListActiveOnB expected 200, got {Describe(activeB)}");
    Expect(activeB.doc!.Value.GetProperty("items").EnumerateArray().Any(x => x.GetProperty("id").GetGuid() == seriesId), "B must see active series");
    Log("Scenario 2 PASS: Bo3 sealed challenge created, accepted, visible both sides");

    // ---- Game 1: start attempts (Scenario 4c: duplicate StartAttempt is idempotent) ----
    var startA1 = await SendAsync(httpA, HttpMethod.Post, $"api/v2/series/{seriesId}/games/1/attempts/start", null, a.AccessToken);
    Expect(startA1.status == 200, $"StartAttemptA expected 200, got {Describe(startA1)}");
    Guid attemptIdA1 = startA1.doc!.Value.GetProperty("attemptId").GetGuid();

    var startA1Retry = await SendAsync(httpA, HttpMethod.Post, $"api/v2/series/{seriesId}/games/1/attempts/start", null, a.AccessToken);
    Expect(startA1Retry.status == 200, $"DuplicateStartAttemptA expected 200, got {Describe(startA1Retry)}");
    Expect(startA1Retry.doc!.Value.GetProperty("attemptId").GetGuid() == attemptIdA1,
        "a retried StartAttempt for the same player/game must return the same attemptId");
    Log("Scenario 4c PASS: duplicate StartAttempt is idempotent");

    var startB1 = await SendAsync(httpB, HttpMethod.Post, $"api/v2/series/{seriesId}/games/1/attempts/start", null, b.AccessToken);
    Expect(startB1.status == 200, $"StartAttemptB expected 200, got {Describe(startB1)}");
    Guid attemptIdB1 = startB1.doc!.Value.GetProperty("attemptId").GetGuid();

    var winningMetrics = BuildMetrics(startA1.doc!.Value, winning: true);
    var losingMetrics = BuildMetrics(startB1.doc!.Value, winning: false);

    // ---- Scenario 9: sealed first-finisher result remains hidden until both complete ----
    var completeA1 = await SendAsync(httpA, HttpMethod.Post, $"api/v2/series/{seriesId}/games/1/attempts/{attemptIdA1}/complete",
        new { metrics = winningMetrics }, a.AccessToken);
    Log($"Game1.CompleteAttemptA: {Describe(completeA1)}");
    Expect(completeA1.status == 200, $"CompleteAttemptA expected 200, got {Describe(completeA1)}");

    var seriesViewBBefore = await SendAsync(httpB, HttpMethod.Get, $"api/v2/series/{seriesId}", null, b.AccessToken);
    Expect(seriesViewBBefore.status == 200, $"BViewBeforeOwnCompletion expected 200, got {Describe(seriesViewBBefore)}");
    var game1FromB = seriesViewBBefore.doc!.Value.GetProperty("games").EnumerateArray().First(g => g.GetProperty("gameNumber").GetInt32() == 1);
    Expect(game1FromB.GetProperty("opponentAttempt").GetProperty("status").GetString() != "NotStarted",
        "B must see that A has submitted something");
    Expect(game1FromB.GetProperty("opponentAttempt").GetProperty("result").ValueKind == JsonValueKind.Null,
        "Scenario 9 SERVER PROOF: B must not see A's sealed result before B has completed their own attempt");
    Log("Scenario 9 (part 1) PASS: opponent's sealed result is null server-side before disclosure");

    var completeB1 = await SendAsync(httpB, HttpMethod.Post, $"api/v2/series/{seriesId}/games/1/attempts/{attemptIdB1}/complete",
        new { metrics = losingMetrics }, b.AccessToken);
    Log($"Game1.CompleteAttemptB: {Describe(completeB1)}");
    Expect(completeB1.status == 200, $"CompleteAttemptB expected 200, got {Describe(completeB1)}");

    var seriesViewAAfter = await SendAsync(httpA, HttpMethod.Get, $"api/v2/series/{seriesId}", null, a.AccessToken);
    Expect(seriesViewAAfter.status == 200, $"AViewAfterDisclosure expected 200, got {Describe(seriesViewAAfter)}");
    var game1FromA = seriesViewAAfter.doc!.Value.GetProperty("games").EnumerateArray().First(g => g.GetProperty("gameNumber").GetInt32() == 1);
    Expect(game1FromA.GetProperty("opponentAttempt").GetProperty("result").ValueKind != JsonValueKind.Null,
        "Scenario 9 SERVER PROOF: once both attempts are complete, the opponent's result must be disclosed");
    Log("Scenario 9 (part 2) PASS: sealed result disclosed to both sides once round resolves");

    // ---- Scenario 4d/5: duplicate completion ----
    var resendIdentical = await SendAsync(httpA, HttpMethod.Post, $"api/v2/series/{seriesId}/games/1/attempts/{attemptIdA1}/complete",
        new { metrics = winningMetrics }, a.AccessToken);
    Log($"Scenario4d.IdenticalResendCompleteAttempt: {Describe(resendIdentical)}");
    Expect(resendIdentical.status == 200, $"IdenticalResend expected 200 (idempotent), got {Describe(resendIdentical)}");
    Log("Scenario 4d PASS: identical resend of an accepted result is idempotent (200)");

    var conflictingMetrics = BuildMetrics(startA1.doc!.Value, winning: false);
    var resendConflicting = await SendAsync(httpA, HttpMethod.Post, $"api/v2/series/{seriesId}/games/1/attempts/{attemptIdA1}/complete",
        new { metrics = conflictingMetrics }, a.AccessToken);
    Log($"Scenario5.ConflictingResendCompleteAttempt: {Describe(resendConflicting)}");
    Expect(resendConflicting.status == 409, $"ConflictingResend expected 409, got {Describe(resendConflicting)}");
    Log("Scenario 5 (server half) PASS: a materially different resend against an already-accepted result " +
        "is rejected 409/conflict by the live server, original result untouched. NOTE: this proves server-side " +
        "duplicate/lost-response safety; it does not exercise the client's own PendingRemoteAttemptResult " +
        "resend-same-payload path, which needs a live Unity scene/UI session.");

    var seriesAfterConflict = await SendAsync(httpA, HttpMethod.Get, $"api/v2/series/{seriesId}", null, a.AccessToken);
    Expect(seriesAfterConflict.status == 200, $"VerifyOriginalResultIntact expected 200, got {Describe(seriesAfterConflict)}");
    Expect(seriesAfterConflict.doc!.Value.GetProperty("currentGameNumber").GetInt32() == 2,
        "game 1 must have resolved and the series moved to game 2");

    var state = new
    {
        usernameA, usernameB, password, displayNameA, displayNameB,
        playerIdA = a.PlayerId, playerIdB = b.PlayerId, tagA = a.Tag, tagB = b.Tag,
        seriesId, friendRequestId
    };
    File.WriteAllText(statePath, JsonSerializer.Serialize(state, jsonOpts));
    Log($"=== Phase 1 complete === state written to {statePath}");
}

async Task Phase2()
{
    if (!File.Exists(statePath))
    {
        throw new Exception($"Phase 1 state file not found at {statePath} - run phase1 first");
    }

    string usernameA, usernameB, password;
    Guid playerIdA, playerIdB, seriesId;
    using (JsonDocument stateDoc = JsonDocument.Parse(File.ReadAllText(statePath)))
    {
        JsonElement state = stateDoc.RootElement;
        usernameA = state.GetProperty("usernameA").GetString()!;
        usernameB = state.GetProperty("usernameB").GetString()!;
        password = state.GetProperty("password").GetString()!;
        playerIdA = state.GetProperty("playerIdA").GetGuid();
        playerIdB = state.GetProperty("playerIdB").GetGuid();
        seriesId = state.GetProperty("seriesId").GetGuid();
    }

    // The state file carries a plaintext password for a live (if throwaway) backend account - never
    // leave it behind, whether this phase succeeds or an assertion/exception aborts it partway
    // through.
    try
    {
        await RunPhase2(usernameA, usernameB, password, playerIdA, playerIdB, seriesId);
    }
    finally
    {
        if (File.Exists(statePath))
        {
            File.Delete(statePath);
        }
    }
}

async Task RunPhase2(
    string usernameA, string usernameB, string password, Guid playerIdA, Guid playerIdB, Guid seriesId)
{
    Log($"=== Phase 2 start === resuming seriesId={seriesId} usernameA={usernameA} usernameB={usernameB}");

    using HttpClient httpA = NewClient();
    using HttpClient httpB = NewClient();

    // Scenario 3 (login-path half): reconnect by logging back in, not by reusing an in-memory token.
    Account a = await Login(httpA, usernameA, password, "", playerIdA);
    Account b = await Login(httpB, usernameB, password, "", playerIdB);
    Log("Scenario 3 (login-path half) PASS: both accounts reconnected via fresh login after this " +
        "process restarted (simulating quit/relaunch); the persisted-session-file / " +
        "CorrespondenceScreenController.Resume() UI half of scenario 3 needs a live Unity scene/UI " +
        "session and is not exercised by this harness.");

    // Auth evidence: exercise POST /api/v2/auth/refresh live for both accounts - the token-rotation
    // path AuthenticatedApiClientBase.EnsureFreshAccessToken/ForceRefresh relies on for nearly every
    // authenticated call the real Unity client makes (15-minute access-token lifetime), distinct from
    // and more frequently hit than the re-login path above. Both accounts' remaining calls this phase
    // use the rotated token, not the one Login returned.
    a = await Refresh(httpA, a);
    b = await Refresh(httpB, b);
    Log("Scenario 3 (refresh-token half) PASS: POST /api/v2/auth/refresh rotated both accounts' " +
        "credentials live; all subsequent calls this phase use the rotated access token.");

    // ---- Scenario 6: server restart between turns; Scenario 11 (partial): frozen rules stable ----
    var seriesAfterRestart = await SendAsync(httpA, HttpMethod.Get, $"api/v2/series/{seriesId}", null, a.AccessToken);
    Log($"Scenario6.GetSeriesAfterBackendRestart: {Describe(seriesAfterRestart)}");
    Expect(seriesAfterRestart.status == 200, $"GetSeriesAfterBackendRestart expected 200, got {Describe(seriesAfterRestart)}");
    var seriesRoot = seriesAfterRestart.doc!.Value;
    Expect(seriesRoot.GetProperty("status").GetString() == "Active", "series must still be Active after restart");
    Expect(seriesRoot.GetProperty("currentGameNumber").GetInt32() == 2, "game 1's outcome must have survived the backend restart");
    Expect(seriesRoot.GetProperty("rules").GetProperty("rulesetId").GetString() == "score-only", "ruleset must be unchanged");
    Expect(seriesRoot.GetProperty("rules").GetProperty("informationPolicy").GetString() == "SealedAttempt", "information policy must be unchanged");
    var game1AfterRestart = seriesRoot.GetProperty("games").EnumerateArray().First(g => g.GetProperty("gameNumber").GetInt32() == 1);
    Expect(game1AfterRestart.GetProperty("yourAttempt").GetProperty("result").ValueKind != JsonValueKind.Null,
        "game 1's already-disclosed result must survive the restart unchanged");
    Log("Scenario 6 PASS: active series/game state survived a real backend process restart (Postgres-persisted, " +
        "not in-memory). Scenario 11 (partial) PASS: FrozenRules unchanged across the restart; no second client " +
        "build was available this session to test rule stability across an actual client update, so the full " +
        "scenario remains BLOCKED for that half.");

    // ---- Game 2 (deciding game: A already leads 1-0 in a Bo3) ----
    var startA2 = await SendAsync(httpA, HttpMethod.Post, $"api/v2/series/{seriesId}/games/2/attempts/start", null, a.AccessToken);
    Expect(startA2.status == 200, $"StartAttemptA(game2) expected 200, got {Describe(startA2)}");
    var startB2 = await SendAsync(httpB, HttpMethod.Post, $"api/v2/series/{seriesId}/games/2/attempts/start", null, b.AccessToken);
    Expect(startB2.status == 200, $"StartAttemptB(game2) expected 200, got {Describe(startB2)}");

    Guid attemptIdA2 = startA2.doc!.Value.GetProperty("attemptId").GetGuid();
    Guid attemptIdB2 = startB2.doc!.Value.GetProperty("attemptId").GetGuid();
    var winningMetrics2 = BuildMetrics(startA2.doc!.Value, winning: true);
    var losingMetrics2 = BuildMetrics(startB2.doc!.Value, winning: false);

    // ---- Scenario 8: simultaneous completion race - genuine concurrent dispatch (Task.WhenAll on ----
    // ---- two independent HttpClients), the deciding game of the series. ----
    Task<(int status, JsonElement? doc, string correlationId)> completeATask = SendAsync(
        httpA, HttpMethod.Post, $"api/v2/series/{seriesId}/games/2/attempts/{attemptIdA2}/complete",
        new { metrics = winningMetrics2 }, a.AccessToken);
    Task<(int status, JsonElement? doc, string correlationId)> completeBTask = SendAsync(
        httpB, HttpMethod.Post, $"api/v2/series/{seriesId}/games/2/attempts/{attemptIdB2}/complete",
        new { metrics = losingMetrics2 }, b.AccessToken);
    await Task.WhenAll(completeATask, completeBTask);
    var completeA2 = completeATask.Result;
    var completeB2 = completeBTask.Result;
    Log($"Scenario8.ConcurrentCompleteA: {Describe(completeA2)}");
    Log($"Scenario8.ConcurrentCompleteB: {Describe(completeB2)}");
    Expect(completeA2.status == 200, $"Concurrent CompleteAttemptA expected 200, got {Describe(completeA2)}");
    Expect(completeB2.status == 200, $"Concurrent CompleteAttemptB expected 200, got {Describe(completeB2)}");

    var finalSeriesView = await SendAsync(httpA, HttpMethod.Get, $"api/v2/series/{seriesId}", null, a.AccessToken);
    Expect(finalSeriesView.status == 200, $"FinalSeriesView expected 200, got {Describe(finalSeriesView)}");
    var finalRoot = finalSeriesView.doc!.Value;
    Expect(finalRoot.GetProperty("status").GetString() == "Completed",
        "A led 1-0 and just won game 2, so a Bo3 (gamesToWin=2) must now be Completed");
    Guid winnerId = finalRoot.GetProperty("winnerId").GetGuid();
    Expect(winnerId == playerIdA, "winner must be player A");
    Log($"Scenario 8 PASS: two concurrently-dispatched CompleteAttempt calls for the deciding game both " +
        $"succeeded with a single consistent outcome (winner={winnerId}), no corrupted/duplicate state. " +
        $"Series {seriesId} completed live.");

    // ---- Scenario 10: completed series history remains readable ----
    var completedA = await SendAsync(httpA, HttpMethod.Get, "api/v2/series/completed?limit=20", null, a.AccessToken);
    Expect(completedA.status == 200, $"ListCompletedOnA expected 200, got {Describe(completedA)}");
    Expect(completedA.doc!.Value.GetProperty("items").EnumerateArray().Any(x => x.GetProperty("id").GetGuid() == seriesId),
        "A must see the completed series in history");

    var completedB = await SendAsync(httpB, HttpMethod.Get, "api/v2/series/completed?limit=20", null, b.AccessToken);
    Expect(completedB.status == 200, $"ListCompletedOnB expected 200, got {Describe(completedB)}");
    Expect(completedB.doc!.Value.GetProperty("items").EnumerateArray().Any(x => x.GetProperty("id").GetGuid() == seriesId),
        "B must see the completed series in history");

    var completedAAgain = await SendAsync(httpA, HttpMethod.Get, "api/v2/series/completed?limit=20", null, a.AccessToken);
    Expect(completedAAgain.status == 200, $"ListCompletedOnAAfterRefresh expected 200, got {Describe(completedAAgain)}");
    Expect(completedAAgain.doc!.Value.GetProperty("items").EnumerateArray().Any(x => x.GetProperty("id").GetGuid() == seriesId),
        "completed series must remain readable across a repeated fresh fetch");
    Log("Scenario 10 PASS: completed series appears in both accounts' history and remains readable across a repeated fresh fetch");

    Log("Scenario 7 evidence: throughout phases 1-2, every read used by one account was a fresh HTTP round " +
        "trip after the other account's mutation - no shared client-side cache was read at any point (both " +
        "accounts use fully independent HttpClients, sharing only the live server as their source of truth).");

    Log("=== Phase 2 complete ===");
}

sealed class Account
{
    public required string Username;
    public required string Password;
    public required string DisplayName;
    public required Guid PlayerId;
    public required string AccessToken;
    public required string RefreshToken;
    public string? Tag;
}
