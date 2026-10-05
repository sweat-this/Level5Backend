using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Level5.Application.Abstractions;
using Level5.Domain.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Level5.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class PasswordRecoveryFlowTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Reset_request_is_anonymous_and_enumeration_safe()
    {
        var client = factory.CreateClient();
        var unknown = await client.PostAsJsonAsync("/api/v2/auth/password-reset/request", new { email = $"unknown{Guid.NewGuid():N}@example.com" });
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);

        var malformed = await client.PostAsJsonAsync("/api/v2/auth/password-reset/request", new { email = "not-an-email" });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task Reset_request_remains_enumeration_safe_when_delivery_provider_faults()
    {
        var player = await factory.RegisterNewPlayerAsync("PasswordResetFault");
        var email = $"resetfault{Guid.NewGuid():N}@example.com";
        var verification = await player.Client.PostAsJsonAsync("/api/v2/me/email/verification", new
        {
            currentPassword = "P@ssw0rd123!",
            email
        });
        Assert.Equal(HttpStatusCode.Accepted, verification.StatusCode);
        var emailToken = factory.Services.GetRequiredService<CapturingEmailVerificationDelivery>()
            .Deliveries.Single(d => d.Destination == email).RawToken;
        Assert.Equal(HttpStatusCode.NoContent, (await factory.CreateClient().PostAsJsonAsync(
            "/api/v2/email-verification/complete", new { token = emailToken })).StatusCode);

        await using var faultingFactory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPasswordRecoveryDelivery>();
            services.AddSingleton<IPasswordRecoveryDelivery, ThrowingPasswordRecoveryDelivery>();
        }));
        using var client = faultingFactory.CreateClient();

        var eligible = await client.PostAsJsonAsync("/api/v2/auth/password-reset/request", new { email });
        var unknown = await client.PostAsJsonAsync(
            "/api/v2/auth/password-reset/request", new { email = $"unknown{Guid.NewGuid():N}@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, eligible.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
    }

    [Fact]
    public async Task Reset_completion_is_single_use_and_invalidates_existing_refresh_sessions()
    {
        var player = await factory.RegisterNewPlayerAsync("PasswordReset");
        var email = $"reset{Guid.NewGuid():N}@example.com";
        var verification = await player.Client.PostAsJsonAsync("/api/v2/me/email/verification", new
        {
            currentPassword = "P@ssw0rd123!",
            email
        });
        Assert.Equal(HttpStatusCode.Accepted, verification.StatusCode);
        var emailToken = factory.Services.GetRequiredService<CapturingEmailVerificationDelivery>()
            .Deliveries.Single(d => d.Destination == email).RawToken;
        Assert.Equal(HttpStatusCode.NoContent, (await factory.CreateClient().PostAsJsonAsync(
            "/api/v2/email-verification/complete", new { token = emailToken })).StatusCode);

        var request = await factory.CreateClient().PostAsJsonAsync("/api/v2/auth/password-reset/request", new { email });
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        var resetToken = factory.Services.GetRequiredService<CapturingPasswordRecoveryDelivery>()
            .Deliveries.Single(d => d.Destination == email).RawToken;

        var complete = await factory.CreateClient().PostAsJsonAsync("/api/v2/auth/password-reset/complete", new
        {
            resetToken,
            newPassword = "N3wP@ssword!"
        });
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);

        var replay = await factory.CreateClient().PostAsJsonAsync("/api/v2/auth/password-reset/complete", new
        {
            resetToken,
            newPassword = "AnotherP@ssword1!"
        });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_password_reset", await ReadCodeAsync(replay));

        var refresh = await factory.CreateClient().PostAsJsonAsync("/api/v2/auth/refresh", new { refreshToken = player.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        // Stateless access-token policy is unchanged: the already-issued JWT remains valid until exp.
        Assert.Equal(HttpStatusCode.OK, (await player.Client.GetAsync("/api/v2/me")).StatusCode);
    }

    [Fact]
    public async Task Password_change_requires_authentication_current_password_and_forces_relogin()
    {
        var anonymous = await factory.CreateClient().PostAsJsonAsync("/api/v2/me/password", new
        {
            currentPassword = "P@ssw0rd123!",
            newPassword = "N3wP@ssword!"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var player = await factory.RegisterNewPlayerAsync("PasswordChange");
        var wrong = await player.Client.PostAsJsonAsync("/api/v2/me/password", new
        {
            currentPassword = "wrong",
            newPassword = "N3wP@ssword!"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_current_password", await ReadCodeAsync(wrong));
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync(
            "/api/v2/auth/refresh", new { refreshToken = player.RefreshToken })).StatusCode);

        var second = await factory.RegisterNewPlayerAsync("PasswordChangeSuccess");
        var changed = await second.Client.PostAsJsonAsync("/api/v2/me/password", new
        {
            currentPassword = "P@ssw0rd123!",
            newPassword = "N3wP@ssword!"
        });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync(
            "/api/v2/auth/refresh", new { refreshToken = second.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.Client.GetAsync("/api/v2/me")).StatusCode);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return body.GetProperty("code").GetString();
    }

    private sealed class ThrowingPasswordRecoveryDelivery : IPasswordRecoveryDelivery
    {
        public Task<PasswordRecoveryDeliveryOutcome> DeliverAsync(
            Email destination,
            string rawToken,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Simulated provider failure.");
    }
}
