using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Level5.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class EmailVerificationFlowTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Private_email_status_requires_authentication_and_starts_empty()
    {
        var anonymous = await factory.CreateClient().GetAsync("/api/v2/me/email");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var player = await factory.RegisterNewPlayerAsync("EmailEmpty");
        var response = await player.Client.GetAsync("/api/v2/me/email");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("email").ValueKind);
        Assert.False(body.GetProperty("isVerified").GetBoolean());
    }

    [Fact]
    public async Task Attaching_email_requires_current_password_and_uses_authenticated_identity()
    {
        var actor = await factory.RegisterNewPlayerAsync("EmailActor");
        var other = await factory.RegisterNewPlayerAsync("EmailOther");
        var otherAccountId = await GetAccountIdAsync(other.Client);
        var email = $"actor{Guid.NewGuid():N}@example.com";

        var wrongPassword = await actor.Client.PostAsJsonAsync("/api/v2/me/email/verification", new
        {
            currentPassword = "wrong",
            email
        });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);

        var attached = await actor.Client.PostAsJsonAsync("/api/v2/me/email/verification", new
        {
            accountId = otherAccountId,
            playerId = other.PlayerId,
            currentPassword = "P@ssw0rd123!",
            email
        });
        Assert.Equal(HttpStatusCode.Accepted, attached.StatusCode);

        var actorStatus = await actor.Client.GetFromJsonAsync<JsonElement>("/api/v2/me/email", JsonOptions);
        var otherStatus = await other.Client.GetFromJsonAsync<JsonElement>("/api/v2/me/email", JsonOptions);
        Assert.Equal(email, actorStatus.GetProperty("email").GetString());
        Assert.Equal(JsonValueKind.Null, otherStatus.GetProperty("email").ValueKind);
    }

    [Fact]
    public async Task Anonymous_completion_verifies_once_and_all_token_failures_are_generic()
    {
        var player = await factory.RegisterNewPlayerAsync("EmailComplete");
        var email = $"complete{Guid.NewGuid():N}@example.com";
        var request = await player.Client.PostAsJsonAsync("/api/v2/me/email/verification", new
        {
            currentPassword = "P@ssw0rd123!",
            email
        });
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        var token = factory.Services.GetRequiredService<CapturingEmailVerificationDelivery>()
            .Deliveries.Single(d => d.Destination == email).RawToken;

        var anonymous = factory.CreateClient();
        var complete = await anonymous.PostAsJsonAsync("/api/v2/email-verification/complete", new { token });
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);

        var status = await player.Client.GetFromJsonAsync<JsonElement>("/api/v2/me/email", JsonOptions);
        Assert.True(status.GetProperty("isVerified").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("verifiedAt").ValueKind);

        var replay = await anonymous.PostAsJsonAsync("/api/v2/email-verification/complete", new { token });
        var unknown = await anonymous.PostAsJsonAsync("/api/v2/email-verification/complete", new { token = "unknown-token" });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var replayCode = await ReadCodeAsync(replay);
        var unknownCode = await ReadCodeAsync(unknown);
        Assert.Equal(replayCode, unknownCode);
        Assert.Equal("invalid_email_verification", replayCode);
    }

    [Fact]
    public async Task Expired_completion_uses_the_generic_failure_contract()
    {
        var player = await factory.RegisterNewPlayerAsync("EmailExpiry");
        var email = $"expiry{Guid.NewGuid():N}@example.com";
        await player.Client.PostAsJsonAsync("/api/v2/me/email/verification", new
        {
            currentPassword = "P@ssw0rd123!",
            email
        });
        var delivery = factory.Services.GetRequiredService<CapturingEmailVerificationDelivery>()
            .Deliveries.Single(d => d.Destination == email);
        var accountId = await GetAccountIdAsync(player.Client);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
            var challenge = await db.EmailVerificationChallenges.SingleAsync(c => c.AccountId == accountId);
            challenge.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/v2/email-verification/complete", new { token = delivery.RawToken });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_email_verification", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Canonical_email_uniqueness_is_authoritative_under_concurrent_attachment()
    {
        var first = await factory.RegisterNewPlayerAsync("EmailRaceOne");
        var second = await factory.RegisterNewPlayerAsync("EmailRaceTwo");
        var email = $"race{Guid.NewGuid():N}@example.com";

        var responses = await Task.WhenAll(
            first.Client.PostAsJsonAsync("/api/v2/me/email/verification", new
            {
                currentPassword = "P@ssw0rd123!",
                email
            }),
            second.Client.PostAsJsonAsync("/api/v2/me/email/verification", new
            {
                currentPassword = "P@ssw0rd123!",
                email = email.ToUpperInvariant()
            }));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
        var conflict = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("email_unavailable", await ReadCodeAsync(conflict));
    }

    [Fact]
    public async Task Resend_rotates_after_cooldown_and_does_not_change_the_target()
    {
        var player = await factory.RegisterNewPlayerAsync("EmailResend");
        var email = $"resend{Guid.NewGuid():N}@example.com";
        await player.Client.PostAsJsonAsync("/api/v2/me/email/verification", new
        {
            currentPassword = "P@ssw0rd123!",
            email
        });

        var tooSoon = await player.Client.PostAsync("/api/v2/me/email/verification/resend", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, tooSoon.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
            var accountId = await GetAccountIdAsync(player.Client);
            var challenge = await db.EmailVerificationChallenges.SingleAsync(c => c.AccountId == accountId);
            challenge.IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-6);
            await db.SaveChangesAsync();
        }

        var resend = await player.Client.PostAsync("/api/v2/me/email/verification/resend", null);
        Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);
        var status = await player.Client.GetFromJsonAsync<JsonElement>("/api/v2/me/email", JsonOptions);
        Assert.Equal(email, status.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Public_player_and_registration_contracts_remain_email_free()
    {
        var player = await factory.RegisterNewPlayerAsync("EmailPrivacy");

        var publicProfile = await player.Client.GetFromJsonAsync<JsonElement>("/api/v2/players/me/profile", JsonOptions);
        Assert.False(publicProfile.TryGetProperty("email", out _));

        var account = await player.Client.GetFromJsonAsync<JsonElement>("/api/v2/me", JsonOptions);
        Assert.False(account.TryGetProperty("email", out _));
    }

    private static async Task<Guid> GetAccountIdAsync(HttpClient client)
    {
        var account = await client.GetFromJsonAsync<JsonElement>("/api/v2/me", JsonOptions);
        return account.GetProperty("accountId").GetGuid();
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return body.GetProperty("code").GetString();
    }
}
