using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Level5.Application.Abstractions;
using Level5.Domain.Identity;
using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Level5.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class AccountSecuritySessionFlowTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Register_login_and_refresh_tokens_bind_to_their_actual_session()
    {
        var registered = await factory.RegisterNewPlayerAsync("SidRegister", "web");
        var registerToken = ReadToken(registered.AccessToken);
        var registerSid = GetSessionId(registerToken);

        var loggedIn = await factory.LoginAsync(registered.Username, "unity");
        var loginToken = ReadToken(loggedIn.AccessToken);
        var loginSid = GetSessionId(loginToken);
        Assert.NotEqual(registerSid, loginSid);
        Assert.Equal(registerToken.Subject, loginToken.Subject);

        var refreshResponse = await factory.CreateClient().PostAsJsonAsync(
            "/api/v2/auth/refresh", new { refreshToken = loggedIn.RefreshToken });
        refreshResponse.EnsureSuccessStatusCode();
        var refreshed = await refreshResponse.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        var refreshedToken = ReadToken(refreshed!.AccessToken);

        Assert.Equal(loginSid, GetSessionId(refreshedToken));
        Assert.NotEqual(loginToken.Id, refreshedToken.Id);
        Assert.Equal(
            [JwtRegisteredClaimNames.Jti, JwtRegisteredClaimNames.Sid, JwtRegisteredClaimNames.Sub],
            refreshedToken.Claims
                .Where(claim => claim.Type is JwtRegisteredClaimNames.Sub or JwtRegisteredClaimNames.Sid or JwtRegisteredClaimNames.Jti)
                .Select(claim => claim.Type)
                .OrderBy(value => value));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        Assert.Equal("Web", (await db.AuthSessions.AsNoTracking().SingleAsync(row => row.Id == registerSid)).ClientKind);
        Assert.Equal("Unity", (await db.AuthSessions.AsNoTracking().SingleAsync(row => row.Id == loginSid)).ClientKind);

        var unrecognized = await factory.RegisterNewPlayerAsync("UnknownHint", "browser-fingerprint-value");
        var unrecognizedSid = GetSessionId(ReadToken(unrecognized.AccessToken));
        Assert.Equal("Unknown", (await db.AuthSessions.AsNoTracking().SingleAsync(row => row.Id == unrecognizedSid)).ClientKind);
    }

    [Fact]
    public async Task Active_session_view_filters_effective_validity_and_exposes_only_safe_fields()
    {
        var current = await factory.RegisterNewPlayerAsync("SessionView", "web");
        var currentSid = GetSessionId(ReadToken(current.AccessToken));
        var accountId = await GetAccountIdAsync(currentSid);
        var now = DateTimeOffset.UtcNow;

        var active = await AddSessionAsync(accountId, now, TimeSpan.FromDays(30), ClientKind.Unity);
        await AddSessionAsync(accountId, now.AddDays(-2), TimeSpan.FromDays(1), ClientKind.Unknown);
        var revoked = await AddSessionAsync(accountId, now, TimeSpan.FromDays(30), ClientKind.Web);
        await RevokeAsync(accountId, revoked.Session.Id, generation: 0, now.AddMinutes(1));
        await AddSessionAsync(accountId, now, TimeSpan.FromDays(30), ClientKind.Unity, sessionGeneration: 99);
        await factory.RegisterNewPlayerAsync("OtherSession");

        var response = await current.Client.GetAsync("/api/v2/me/sessions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.GetProperty("sessionId").GetGuid() == currentSid && item.GetProperty("isCurrent").GetBoolean());
        Assert.Contains(items, item => item.GetProperty("sessionId").GetGuid() == active.Session.Id.Value && !item.GetProperty("isCurrent").GetBoolean());

        var expectedFields = new[] { "clientKind", "createdAt", "expiresAt", "isCurrent", "lastRefreshedAt", "sessionId" };
        foreach (var item in items)
        {
            Assert.Equal(expectedFields, item.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
            var serialized = item.GetRawText();
            Assert.DoesNotContain("refreshToken", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("generation", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("accountId", serialized, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Legacy_sub_only_token_still_works_elsewhere_but_requires_reauthentication_for_session_management()
    {
        var registered = await factory.RegisterNewPlayerAsync("LegacySid");
        var accountId = ReadToken(registered.AccessToken).Subject;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateLegacyToken(accountId));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v2/me")).StatusCode);

        var response = await client.GetAsync("/api/v2/me/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("reauthentication_required", problem.GetProperty("code").GetString());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateLegacyToken(accountId, "not-a-guid"));
        var malformedResponse = await client.GetAsync("/api/v2/me/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, malformedResponse.StatusCode);
        var malformedProblem = await malformedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("reauthentication_required", malformedProblem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Revoke_one_is_idempotent_and_the_target_can_no_longer_refresh()
    {
        var current = await factory.RegisterNewPlayerAsync("RevokeOne");
        var target = await factory.LoginAsync(current.Username);
        var targetSid = GetSessionId(ReadToken(target.AccessToken));

        var first = await current.Client.DeleteAsync($"/api/v2/me/sessions/{targetSid}");
        var second = await current.Client.DeleteAsync($"/api/v2/me/sessions/{targetSid}");
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        var refresh = await factory.CreateClient().PostAsJsonAsync(
            "/api/v2/auth/refresh", new { refreshToken = target.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Revoke_others_preserves_current_and_revokes_every_other_active_session()
    {
        var current = await factory.RegisterNewPlayerAsync("RevokeOthers");
        var second = await factory.LoginAsync(current.Username);
        var third = await factory.LoginAsync(current.Username);

        var response = await current.Client.PostAsync("/api/v2/me/sessions/revoke-others", content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(current.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(second.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(third.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Revoke_all_advances_generation_and_requires_a_new_sign_in_session()
    {
        var current = await factory.RegisterNewPlayerAsync("RevokeAll");
        var second = await factory.LoginAsync(current.Username);
        var accountId = Guid.Parse(ReadToken(current.AccessToken).Subject);

        var response = await current.Client.PostAsync("/api/v2/me/sessions/revoke-all", content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(current.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(second.RefreshToken)).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
            Assert.Equal(1, (await db.Accounts.AsNoTracking().SingleAsync(row => row.Id == accountId)).SessionGeneration);
        }

        var replacement = await factory.LoginAsync(current.Username);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(replacement.RefreshToken)).StatusCode);

        // The still-unexpired old access JWT is valid on ordinary APIs but its stale sid cannot
        // administer the newer generation.
        Assert.Equal(HttpStatusCode.OK, (await current.Client.GetAsync("/api/v2/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await current.Client.GetAsync("/api/v2/me/sessions")).StatusCode);
    }

    [Fact]
    public async Task Logged_out_session_cannot_list_or_revoke_a_newer_active_session()
    {
        var old = await factory.RegisterNewPlayerAsync("GuardLogout");
        var logout = await factory.CreateClient().PostAsJsonAsync(
            "/api/v2/auth/logout", new { refreshToken = old.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var replacement = await factory.LoginAsync(old.Username);
        var replacementSid = GetSessionId(ReadToken(replacement.AccessToken));

        Assert.Equal(HttpStatusCode.Unauthorized, (await old.Client.GetAsync("/api/v2/me/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await old.Client.DeleteAsync($"/api/v2/me/sessions/{replacementSid}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(replacement.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Specifically_revoked_session_cannot_list_or_revoke_a_newer_active_session()
    {
        var owner = await factory.RegisterNewPlayerAsync("GuardRevoked");
        var revoked = await factory.LoginAsync(owner.Username);
        var revokedSid = GetSessionId(ReadToken(revoked.AccessToken));
        var replacement = await factory.LoginAsync(owner.Username);
        var replacementSid = GetSessionId(ReadToken(replacement.AccessToken));

        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.Client.DeleteAsync($"/api/v2/me/sessions/{revokedSid}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await revoked.Client.GetAsync("/api/v2/me/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await revoked.Client.DeleteAsync($"/api/v2/me/sessions/{replacementSid}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(replacement.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Password_invalidated_session_cannot_list_or_revoke_a_newer_generation_session()
    {
        var old = await factory.RegisterNewPlayerAsync("GuardPassword");
        var changed = await old.Client.PostAsJsonAsync("/api/v2/me/password", new
        {
            currentPassword = "P@ssw0rd123!",
            newPassword = "N3wP@ssword!"
        });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        var replacement = await factory.LoginAsync(old.Username, password: "N3wP@ssword!");
        var replacementSid = GetSessionId(ReadToken(replacement.AccessToken));

        Assert.Equal(HttpStatusCode.Unauthorized, (await old.Client.GetAsync("/api/v2/me/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await old.Client.DeleteAsync($"/api/v2/me/sessions/{replacementSid}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(replacement.RefreshToken)).StatusCode);
    }

    private async Task<(AuthSession Session, string RawToken)> AddSessionAsync(
        Guid accountId,
        DateTimeOffset createdAt,
        TimeSpan lifetime,
        ClientKind clientKind,
        long sessionGeneration = 0)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var generator = scope.ServiceProvider.GetRequiredService<IRefreshTokenGenerator>();
        var generated = generator.Generate();
        var session = AuthSession.Create(
            new(accountId), generated.Hash, createdAt, lifetime, sessionGeneration, clientKind);
        var store = new AuthSessionStore(scope.ServiceProvider.GetRequiredService<Level5V2DbContext>());
        await store.AddAsync(session, default);
        await scope.ServiceProvider.GetRequiredService<Level5V2DbContext>().SaveChangesAsync();
        return (session, generated.RawValue);
    }

    private async Task RevokeAsync(Guid accountId, Level5.Domain.Ids.AuthSessionId sessionId, long generation, DateTimeOffset now)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = new AuthSessionStore(scope.ServiceProvider.GetRequiredService<Level5V2DbContext>());
        await store.RevokeActiveAsync(new(accountId), sessionId, generation, now, default);
    }

    private async Task<Guid> GetAccountIdAsync(Guid sessionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        return await db.AuthSessions.Where(row => row.Id == sessionId).Select(row => row.AccountId).SingleAsync();
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken)
        => factory.CreateClient().PostAsJsonAsync("/api/v2/auth/refresh", new { refreshToken });

    private static JwtSecurityToken ReadToken(string value) => new JwtSecurityTokenHandler().ReadJwtToken(value);

    private static Guid GetSessionId(JwtSecurityToken token)
        => Guid.Parse(token.Claims.Single(claim => claim.Type == JwtRegisteredClaimNames.Sid).Value);

    private static string CreateLegacyToken(string accountId, string? sessionId = null)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-only-signing-key-not-for-production-use-32chars-min")),
            SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, accountId),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString())
        };
        if (sessionId is not null)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Sid, sessionId));
        }

        var token = new JwtSecurityToken(
            issuer: "Level5BackendV2.Tests",
            audience: "Level5Client.Tests",
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed record AuthResponse(
        string AccessToken,
        DateTimeOffset ExpiresAt,
        Guid PlayerId,
        string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAt);
}
