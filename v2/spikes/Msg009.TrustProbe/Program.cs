using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

// Isolated MSG-009 experiment. Not referenced by the solution or any production assembly.
return await Probe.RunAsync(args);

internal static class Probe
{
    private const string Issuer = "https://player-auth.services.api.unity.com";
    private const int MaximumResponseBytes = 65_536;

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.SequenceEqual(["--self-test"])) return SelfTest();
            var localBackend = args.SequenceEqual(["--live-local"]);
            if (!localBackend && !args.SequenceEqual(["--live"]))
            {
                Console.WriteLine("Usage: --self-test | --live | --live-local. See the MSG-009 probe runbook.");
                return 2;
            }

            // Validate all configuration before making any requests. Never print credentials or responses.
            var project = RequiredGuid("MSG009_PROJECT_ID");
            var environment = RequiredGuid("MSG009_ENVIRONMENT_ID");
            var backendPlayer = RequiredGuid("MSG009_EXPECTED_BACKEND_PLAYER_ID");
            var host = Required("MSG009_EXPECTED_UGS_HOST_ID");
            var lobby = Required("MSG009_LOBBY_ID");
            Require(lobby.Length <= 64 && lobby.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
            var origin = ValidateOrigin(Required("MSG009_BACKEND_ORIGIN"), localBackend);
            var backendToken = Required("MSG009_BACKEND_ACCESS_TOKEN");
            var unityToken = Required("MSG009_UGS_ACCESS_TOKEN");
            Require(backendToken.Length <= 8192 && unityToken.Length <= 8192);

            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            using var client = new HttpClient(handler);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var me = await GetJsonAsync(client, new Uri(origin, "api/v2/me"), backendToken, timeout.Token);
            ValidateAccount(me.RootElement, backendPlayer);
            using var jwks = await GetJsonAsync(client, new Uri(Issuer + "/.well-known/jwks.json"), null, timeout.Token);
            var keys = new JsonWebKeySet(jwks.RootElement.GetRawText()).GetSigningKeys();
            var subject = ValidateToken(unityToken, keys, project, environment);
            using var membership = await GetJsonAsync(client,
                new Uri("https://lobby.services.api.unity.com/v1/" + lobby), unityToken, timeout.Token);
            ValidateMembership(membership.RootElement, lobby, project, environment, subject, host);
            Require(ValidateToken(unityToken, keys, project, environment) == subject);
            // The two remote reads are observations, not an atomic registration or authorization lease.
            Console.WriteLine("OBSERVED: authenticated Backend account and signed UGS subject in the requested private lobby.");
            Console.WriteLine("NOT CERTIFIED: durable binding, revocation races, host liveness, messaging and release gates.");
            return 0;
        }
        catch (Exception)
        {
            // IdentityModel/HTTP/JSON exception text can contain private values. No exception telemetry here.
            Console.WriteLine("BLOCKED: probe rejected configuration, credentials, response or service availability. No binding created.");
            return 2;
        }
    }

    private static string Required(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException();

    private static Guid RequiredGuid(string name)
        => Guid.TryParse(Required(name), out var value) && value != Guid.Empty ? value : throw new InvalidOperationException();

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException();
    }

    private static Uri ValidateOrigin(string value, bool localBackend)
    {
        var origin = new Uri(value, UriKind.Absolute);
        var numericLoopback = origin.Host is "127.0.0.1" or "[::1]";
        Require((origin.Scheme == "https" || (localBackend && origin.Scheme == "http" && numericLoopback)) &&
            origin.UserInfo.Length == 0 && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0);
        return origin;
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, Uri uri, string? token, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        Require(response.StatusCode == HttpStatusCode.OK &&
            response.Content.Headers.ContentType?.MediaType == "application/json");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellation)) != 0)
        {
            Require(bytes.Length + count <= MaximumResponseBytes);
            bytes.Write(buffer, 0, count);
        }
        var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        try { RejectDuplicateProperties(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Require(names.Add(property.Name));
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static void ValidateAccount(JsonElement me, Guid expectedPlayer)
    {
        Require(me.GetProperty("playerId").GetGuid() == expectedPlayer &&
            me.GetProperty("accountId").GetGuid() != Guid.Empty && me.GetProperty("status").GetString() == "Active");
    }

    private static string ValidateToken(string token, IEnumerable<SecurityKey> keys, Guid project, Guid environment)
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 8192 };
        handler.ValidateToken(token, new TokenValidationParameters
        {
            RequireSignedTokens = true, ValidateIssuerSigningKey = true, IssuerSigningKeys = keys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidateIssuer = true, ValidIssuer = Issuer,
            ValidateAudience = true, ValidAudience = "upid:" + project.ToString("D"),
            RequireExpirationTime = true, ValidateLifetime = true, ClockSkew = TimeSpan.Zero
        }, out var validated);
        var jwt = (JwtSecurityToken)validated;
        Require(jwt.Payload.TryGetValue("project_id", out var projectClaim) &&
            projectClaim is string text && text == project.ToString("D") &&
            jwt.Audiences.Contains("envId:" + environment.ToString("D"), StringComparer.Ordinal));
        // Candidate claim profile, deliberately strict. Live evidence must confirm it before adoption.
        var subjects = jwt.Claims.Where(claim => claim.Type == "sub").ToArray();
        Require(subjects.Length == 1 && !string.IsNullOrWhiteSpace(subjects[0].Value));
        return subjects[0].Value;
    }

    private static void ValidateMembership(JsonElement membership, string lobby, Guid project, Guid environment,
        string subject, string originalHost)
    {
        Require(membership.GetProperty("id").GetString() == lobby &&
            membership.GetProperty("upid").GetGuid() == project &&
            membership.GetProperty("environmentId").GetGuid() == environment &&
            membership.GetProperty("isPrivate").GetBoolean() &&
            membership.GetProperty("maxPlayers").GetInt32() == 5 &&
            membership.GetProperty("hostId").GetString() == originalHost);
        var players = membership.GetProperty("players").EnumerateArray()
            .Select(player => player.GetProperty("id").GetString()).ToArray();
        Require(players.Length is >= 1 and <= 5 && players.All(id => !string.IsNullOrWhiteSpace(id)) &&
            players.Distinct(StringComparer.Ordinal).Count() == players.Length &&
            players.Contains(subject, StringComparer.Ordinal) && players.Contains(originalHost, StringComparer.Ordinal));
    }

    private static int SelfTest()
    {
        var project = Guid.NewGuid();
        var environment = Guid.NewGuid();
        var account = Guid.NewGuid();
        using var signer = RSA.Create(2048);
        using var attacker = RSA.Create(2048);
        var trusted = new RsaSecurityKey(signer) { KeyId = "fixture" };
        var untrusted = new RsaSecurityKey(attacker) { KeyId = "fixture" };
        var now = DateTime.UtcNow;
        string Token(SecurityKey key, string issuer = Issuer, Guid? tokenProject = null,
            Guid? tokenEnvironment = null, DateTime? expires = null, DateTime? notBefore = null,
            string subject = "member", string algorithm = SecurityAlgorithms.RsaSha256,
            bool includeExpiration = true, Guid? claimProject = null)
        {
            var payload = new JwtPayload(issuer, null,
                [new Claim("sub", subject), new Claim("project_id", (claimProject ?? tokenProject ?? project).ToString("D"))],
                notBefore ?? now.AddMinutes(-1), expires ?? now.AddMinutes(5), now.AddMinutes(-1));
            payload["aud"] = new[] { "upid:" + (tokenProject ?? project).ToString("D"),
                "envId:" + (tokenEnvironment ?? environment).ToString("D") };
            if (!includeExpiration) payload.Remove("exp");
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                new JwtHeader(new SigningCredentials(key, algorithm)), payload));
        }
        JsonDocument Membership(string subject = "member", string host = "host", Guid? scope = null,
            string lobby = "lobby", bool isPrivate = true, int maxPlayers = 5)
            => JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                id = lobby, upid = project, environmentId = scope ?? environment, isPrivate, maxPlayers,
                hostId = host, players = new[] { new { id = "host" }, new { id = subject } }
            }));
        var passed = 0;
        void Accept(Action assertion) { assertion(); passed++; }
        void Reject(Action assertion)
        {
            try { assertion(); } catch (Exception) { passed++; return; }
            throw new InvalidOperationException("A negative probe assertion unexpectedly accepted.");
        }
        string Verify(string token) => ValidateToken(token, [trusted], project, environment);
        void VerifyMembership(JsonDocument document)
        {
            using (document) ValidateMembership(document.RootElement, "lobby", project, environment, "member", "host");
        }

        Accept(() => Require(Verify(Token(trusted)) == "member"));
        Reject(() => Verify(Token(untrusted)));
        Reject(() => Verify(Token(trusted, issuer: "https://attacker.invalid")));
        Reject(() => Verify(Token(trusted, tokenProject: Guid.NewGuid())));
        Reject(() => Verify(Token(trusted, claimProject: Guid.NewGuid())));
        Reject(() => Verify(Token(trusted, tokenEnvironment: Guid.NewGuid())));
        Reject(() => Verify(Token(trusted, expires: now.AddSeconds(-1))));
        Reject(() => Verify(Token(trusted, notBefore: now.AddMinutes(1))));
        Reject(() => Verify(Token(trusted, subject: "")));
        Reject(() => Verify(Token(trusted, algorithm: SecurityAlgorithms.RsaSha512)));
        Reject(() => Verify(Token(trusted, includeExpiration: false)));
        Reject(() => Verify(new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            new JwtHeader(), new JwtPayload(Issuer, "upid:" + project.ToString("D"),
                [new Claim("sub", "member")], now.AddMinutes(-1), now.AddMinutes(5))))));
        Reject(() => Verify("host-asserted-member-and-transport-nonce"));
        Accept(() => VerifyMembership(Membership()));
        Reject(() => VerifyMembership(Membership(subject: "different-member")));
        Reject(() => VerifyMembership(Membership(host: "replacement-host")));
        Reject(() => VerifyMembership(Membership(scope: Guid.NewGuid())));
        Reject(() => VerifyMembership(Membership(lobby: "different-lobby")));
        Reject(() => VerifyMembership(Membership(isPrivate: false)));
        Reject(() => VerifyMembership(Membership(maxPlayers: 6)));
        Reject(() => VerifyMembership(Membership(subject: "host")));
        using var active = JsonDocument.Parse(JsonSerializer.Serialize(new { playerId = account, accountId = Guid.NewGuid(), status = "Active" }));
        Accept(() => ValidateAccount(active.RootElement, account));
        Reject(() => ValidateAccount(active.RootElement, Guid.NewGuid()));
        using var disabled = JsonDocument.Parse(JsonSerializer.Serialize(new { playerId = account, accountId = Guid.NewGuid(), status = "Disabled" }));
        Reject(() => ValidateAccount(disabled.RootElement, account));
        using var duplicate = JsonDocument.Parse("{\"id\":\"first\",\"id\":\"second\"}");
        Reject(() => RejectDuplicateProperties(duplicate.RootElement));
        Accept(() => ValidateOrigin("http://127.0.0.1:5000", true));
        Accept(() => ValidateOrigin("http://[::1]:5000", true));
        Accept(() => ValidateOrigin("https://backend.example", false));
        Reject(() => ValidateOrigin("http://127.0.0.1:5000", false));
        Reject(() => ValidateOrigin("http://backend.example", true));
        Reject(() => ValidateOrigin("https://backend.example/api", false));
        Reject(() => ValidateOrigin("https://user:password@backend.example", false));
        Console.WriteLine($"PASS: {passed} offline cryptographic/response assertions. No provider or Backend contacted; no live certification.");
        return 0;
    }
}
