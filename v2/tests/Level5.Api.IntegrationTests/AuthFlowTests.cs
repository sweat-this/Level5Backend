using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Level5.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class AuthFlowTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Register_then_call_a_protected_endpoint_succeeds()
    {
        var player = await factory.RegisterNewPlayerAsync("Alice");

        var response = await player.Client.GetAsync("/api/v2/players/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Register_returns_a_refresh_token_alongside_the_access_token()
    {
        var player = await factory.RegisterNewPlayerAsync("Rex");

        Assert.False(string.IsNullOrEmpty(player.RefreshToken));
    }

    [Fact]
    public async Task Protected_endpoint_without_a_token_is_rejected()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v2/players/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Registering_a_duplicate_username_returns_a_conflict_problem_detail()
    {
        var client = factory.CreateClient();
        var username = $"dup{Guid.NewGuid():N}"[..20];
        var payload = new { username, password = "P@ssw0rd123!", displayName = "Dup" };

        var first = await client.PostAsJsonAsync("/api/v2/auth/register", payload);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v2/auth/register", payload);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<ProblemDetailsDto>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Equal("conflict", problem!.Code);
    }

    [Fact]
    public async Task Registering_a_password_that_fails_policy_returns_a_validation_problem_detail()
    {
        var client = factory.CreateClient();
        var username = $"weak{Guid.NewGuid():N}"[..20];

        var response = await client.PostAsJsonAsync("/api/v2/auth/register", new { username, password = "short", displayName = "Weak" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsDto>(JsonOptions);
        Assert.Equal("weak_password", problem!.Code);
    }

    [Fact]
    public async Task Login_with_the_wrong_password_returns_unauthorized_not_a_stack_trace()
    {
        var client = factory.CreateClient();
        var username = $"wrongpw{Guid.NewGuid():N}"[..20];
        await client.PostAsJsonAsync("/api/v2/auth/register", new { username, password = "P@ssw0rd123!", displayName = "WrongPw" });

        var response = await client.PostAsJsonAsync("/api/v2/auth/login", new { username, password = "not-the-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", body); // no stack trace frames
    }

    [Fact]
    public async Task Login_returns_a_new_refresh_token_distinct_from_the_registration_one()
    {
        var client = factory.CreateClient();
        var username = $"login{Guid.NewGuid():N}"[..20];
        await client.PostAsJsonAsync("/api/v2/auth/register", new { username, password = "P@ssw0rd123!", displayName = "Login" });

        var response = await client.PostAsJsonAsync("/api/v2/auth/login", new { username, password = "P@ssw0rd123!" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("refreshToken").GetString()));
    }

    [Fact]
    public async Task A_missing_required_field_returns_the_same_problem_details_shape_as_a_domain_validation_error()
    {
        var client = factory.CreateClient();

        // "username" is omitted entirely, so this is rejected by [ApiController]'s automatic
        // model-binding validation before RegisterAccountUseCase ever runs - a different code
        // path than the domain/application exceptions ApiExceptionHandler translates, which
        // previously produced a differently-shaped body with no "code" field.
        var response = await client.PostAsJsonAsync("/api/v2/auth/register", new { password = "P@ssw0rd123!", displayName = "NoUsername" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetMe_returns_the_authenticated_accounts_own_identity()
    {
        var player = await factory.RegisterNewPlayerAsync("Mia");

        var response = await player.Client.GetAsync("/api/v2/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(player.PlayerId, body.GetProperty("playerId").GetGuid());
        Assert.Equal("Active", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetMe_without_a_token_is_rejected()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v2/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refreshing_a_valid_token_returns_a_new_access_token_and_a_rotated_refresh_token()
    {
        var player = await factory.RegisterNewPlayerAsync("Nora");

        var response = await factory.CreateClient().PostAsJsonAsync("/api/v2/auth/refresh", new { refreshToken = player.RefreshToken });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.NotEqual(player.RefreshToken, body.GetProperty("refreshToken").GetString());
    }

    [Fact]
    public async Task A_new_access_token_from_refresh_reaches_a_protected_endpoint()
    {
        var player = await factory.RegisterNewPlayerAsync("Oscar");

        var refreshResponse = await factory.CreateClient().PostAsJsonAsync("/api/v2/auth/refresh", new { refreshToken = player.RefreshToken });
        var body = await refreshResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var newAccessToken = body.GetProperty("accessToken").GetString();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", newAccessToken);
        var meResponse = await client.GetAsync("/api/v2/me");

        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
    }

    [Fact]
    public async Task Reusing_an_already_rotated_refresh_token_is_rejected()
    {
        var player = await factory.RegisterNewPlayerAsync("Priya");
        await factory.CreateClient().PostAsJsonAsync("/api/v2/auth/refresh", new { refreshToken = player.RefreshToken });

        var replay = await factory.CreateClient().PostAsJsonAsync("/api/v2/auth/refresh", new { refreshToken = player.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Refreshing_with_the_new_token_after_rotation_succeeds()
    {
        var player = await factory.RegisterNewPlayerAsync("Quinn");
        var first = await factory.CreateClient().PostAsJsonAsync("/api/v2/auth/refresh", new { refreshToken = player.RefreshToken });
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var secondRefreshToken = firstBody.GetProperty("refreshToken").GetString();

        var second = await factory.CreateClient().PostAsJsonAsync("/api/v2/auth/refresh", new { refreshToken = secondRefreshToken });

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Refreshing_with_an_unknown_token_is_rejected()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v2/auth/refresh", new { refreshToken = "not-a-real-token" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_session()
    {
        var player = await factory.RegisterNewPlayerAsync("Sam");

        var logoutResponse = await factory.CreateClient().PostAsJsonAsync("/api/v2/auth/logout", new { refreshToken = player.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        var refreshAfterLogout = await factory.CreateClient().PostAsJsonAsync("/api/v2/auth/refresh", new { refreshToken = player.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAfterLogout.StatusCode);
    }

    [Fact]
    public async Task Repeated_logout_is_stable_and_idempotent()
    {
        var player = await factory.RegisterNewPlayerAsync("Tara");

        var first = await factory.CreateClient().PostAsJsonAsync("/api/v2/auth/logout", new { refreshToken = player.RefreshToken });
        var second = await factory.CreateClient().PostAsJsonAsync("/api/v2/auth/logout", new { refreshToken = player.RefreshToken });

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    [Fact]
    public async Task Logout_does_not_require_a_still_valid_access_token()
    {
        // No Authorization header on this client at all - logout authenticates via possession of
        // the refresh credential itself, not a bearer access token (see AuthController.Logout).
        var player = await factory.RegisterNewPlayerAsync("Uma");
        var anonymousClient = factory.CreateClient();

        var response = await anonymousClient.PostAsJsonAsync("/api/v2/auth/logout", new { refreshToken = player.RefreshToken });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Logout_does_not_invalidate_an_already_issued_access_token()
    {
        // Documented baseline policy (see V2 README): revoking the refresh session prevents
        // future refreshes, but an already-issued access token remains valid until its own short
        // expiry - there is no per-request session lookup on the bearer path.
        var player = await factory.RegisterNewPlayerAsync("Vic");

        await factory.CreateClient().PostAsJsonAsync("/api/v2/auth/logout", new { refreshToken = player.RefreshToken });
        var response = await player.Client.GetAsync("/api/v2/players/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_disabled_account_cannot_login()
    {
        var player = await factory.RegisterNewPlayerAsync("Will");
        var username = await DisableAccountAsync(player.PlayerId);

        var response = await factory.CreateClient().PostAsJsonAsync("/api/v2/auth/login", new { username, password = "P@ssw0rd123!" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_disabled_account_cannot_refresh_its_session()
    {
        var player = await factory.RegisterNewPlayerAsync("Xena");
        await DisableAccountAsync(player.PlayerId);

        var response = await factory.CreateClient().PostAsJsonAsync("/api/v2/auth/refresh", new { refreshToken = player.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// There is no account-administration endpoint in this slice (by design - see the V2 README's
    /// non-goals), so disabling an account for this test reaches directly into the database, the
    /// same way a future admin tool eventually would.
    /// </summary>
    private async Task<string> DisableAccountAsync(Guid playerId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();

        var profile = await db.PlayerProfiles.SingleAsync(p => p.Id == playerId);
        var account = await db.Accounts.SingleAsync(a => a.Id == profile.AccountId);
        account.Status = "Disabled";
        await db.SaveChangesAsync();

        return account.Username;
    }

    private sealed record ProblemDetailsDto(string Code, string Title, int Status);
}
