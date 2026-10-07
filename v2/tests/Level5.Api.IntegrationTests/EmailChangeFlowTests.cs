using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Level5.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class EmailChangeFlowTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Pending_replacement_is_visible_while_verified_email_remains_canonical()
    {
        var (player, currentEmail) = await RegisterVerifiedAsync("EmailChangeStatus");
        var replacement = $"replace{Guid.NewGuid():N}@example.com";

        var response = await RequestChangeAsync(player.Client, replacement);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var status = await player.Client.GetFromJsonAsync<JsonElement>("/api/v2/me/email", JsonOptions);
        Assert.Equal(currentEmail, status.GetProperty("email").GetString());
        Assert.True(status.GetProperty("isVerified").GetBoolean());
        Assert.Equal(replacement, status.GetProperty("pendingEmail").GetString());
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("pendingEmailChangeExpiresAt").ValueKind);
        Assert.False(status.GetProperty("pendingEmailChangeExpired").GetBoolean());
    }

    [Fact]
    public async Task Initiation_requires_current_password_and_candidate_is_reserved_concurrently()
    {
        var (first, _) = await RegisterVerifiedAsync("EmailChangeRaceOne");
        var (second, _) = await RegisterVerifiedAsync("EmailChangeRaceTwo");
        var target = $"reserved{Guid.NewGuid():N}@example.com";

        var wrong = await first.Client.PostAsJsonAsync("/api/v2/me/email/change", new
        {
            currentPassword = "wrong",
            newEmail = target
        });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var responses = await Task.WhenAll(
            RequestChangeAsync(first.Client, target),
            RequestChangeAsync(second.Client, target.ToUpperInvariant()));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Accepted);
        var conflict = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("email_unavailable", await ReadCodeAsync(conflict));
    }

    [Fact]
    public async Task Resend_supersedes_old_token_and_completion_promotes_replacement()
    {
        var (player, oldEmail) = await RegisterVerifiedAsync("EmailChangeComplete");
        var target = $"complete{Guid.NewGuid():N}@example.com";
        await RequestChangeAsync(player.Client, target);

        var delivery = factory.Services.GetRequiredService<CapturingEmailChangeDelivery>();
        var firstToken = delivery.Verifications.Last(v => v.Destination == target).RawToken;

        await BackdateChallengeAsync(player.Client, minutes: 6);
        var resend = await player.Client.PostAsync("/api/v2/me/email/change/resend", null);
        Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);
        var secondToken = delivery.Verifications.Last(v => v.Destination == target).RawToken;
        Assert.NotEqual(firstToken, secondToken);

        var anonymous = factory.CreateClient();
        var superseded = await anonymous.PostAsJsonAsync("/api/v2/email-change/complete", new { token = firstToken });
        Assert.Equal(HttpStatusCode.BadRequest, superseded.StatusCode);
        Assert.Equal("invalid_email_change", await ReadCodeAsync(superseded));

        var complete = await anonymous.PostAsJsonAsync("/api/v2/email-change/complete", new { token = secondToken });
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);

        var status = await player.Client.GetFromJsonAsync<JsonElement>("/api/v2/me/email", JsonOptions);
        Assert.Equal(target, status.GetProperty("email").GetString());
        Assert.True(status.GetProperty("isVerified").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("pendingEmail").ValueKind);
        Assert.Contains(oldEmail, delivery.PreviousAddressNotifications);

        var replay = await anonymous.PostAsJsonAsync("/api/v2/email-change/complete", new { token = secondToken });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_email_change", await ReadCodeAsync(replay));
    }

    [Fact]
    public async Task Cancel_invalidates_pending_token_without_changing_verified_email()
    {
        var (player, oldEmail) = await RegisterVerifiedAsync("EmailChangeCancel");
        var target = $"cancel{Guid.NewGuid():N}@example.com";
        await RequestChangeAsync(player.Client, target);
        var token = factory.Services.GetRequiredService<CapturingEmailChangeDelivery>()
            .Verifications.Last(v => v.Destination == target).RawToken;

        var cancel = await player.Client.DeleteAsync("/api/v2/me/email/change");
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);

        var status = await player.Client.GetFromJsonAsync<JsonElement>("/api/v2/me/email", JsonOptions);
        Assert.Equal(oldEmail, status.GetProperty("email").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("pendingEmail").ValueKind);

        var completion = await factory.CreateClient().PostAsJsonAsync(
            "/api/v2/email-change/complete", new { token });
        Assert.Equal(HttpStatusCode.BadRequest, completion.StatusCode);
        Assert.Equal("invalid_email_change", await ReadCodeAsync(completion));
    }

    [Fact]
    public async Task Expired_pending_change_is_reported_and_cannot_complete()
    {
        var (player, oldEmail) = await RegisterVerifiedAsync("EmailChangeExpiry");
        var target = $"expired{Guid.NewGuid():N}@example.com";
        await RequestChangeAsync(player.Client, target);
        var token = factory.Services.GetRequiredService<CapturingEmailChangeDelivery>()
            .Verifications.Last(v => v.Destination == target).RawToken;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
            var accountId = await GetAccountIdAsync(player.Client);
            var challenge = await db.EmailVerificationChallenges.SingleAsync(c => c.AccountId == accountId);
            challenge.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var status = await player.Client.GetFromJsonAsync<JsonElement>("/api/v2/me/email", JsonOptions);
        Assert.Equal(oldEmail, status.GetProperty("email").GetString());
        Assert.Equal(target, status.GetProperty("pendingEmail").GetString());
        Assert.True(status.GetProperty("pendingEmailChangeExpired").GetBoolean());

        var completion = await factory.CreateClient().PostAsJsonAsync(
            "/api/v2/email-change/complete", new { token });
        Assert.Equal(HttpStatusCode.BadRequest, completion.StatusCode);
        Assert.Equal("invalid_email_change", await ReadCodeAsync(completion));
    }

    [Fact]
    public async Task Previous_address_notification_failure_never_rolls_back_promotion()
    {
        var (player, _) = await RegisterVerifiedAsync("EmailChangeNotifyFailure");
        var target = $"notify{Guid.NewGuid():N}@example.com";
        await RequestChangeAsync(player.Client, target);
        var delivery = factory.Services.GetRequiredService<CapturingEmailChangeDelivery>();
        var token = delivery.Verifications.Last(v => v.Destination == target).RawToken;
        delivery.ThrowOnPreviousAddressNotification = true;

        try
        {
            var complete = await factory.CreateClient().PostAsJsonAsync(
                "/api/v2/email-change/complete", new { token });
            Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        }
        finally
        {
            delivery.ThrowOnPreviousAddressNotification = false;
        }

        var status = await player.Client.GetFromJsonAsync<JsonElement>("/api/v2/me/email", JsonOptions);
        Assert.Equal(target, status.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Unexpired_replacement_reservation_cannot_be_stolen()
    {
        var (owner, _) = await RegisterVerifiedAsync("EmailChangeActiveOwner");
        var (contender, _) = await RegisterVerifiedAsync("EmailChangeActiveContender");
        var target = $"active{Guid.NewGuid():N}@example.com";
        await RequestChangeAsync(owner.Client, target);
        var token = factory.Services.GetRequiredService<CapturingEmailChangeDelivery>()
            .Verifications.Last(v => v.Destination == target).RawToken;

        var conflict = await RequestChangeAsync(contender.Client, target.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("email_unavailable", await ReadCodeAsync(conflict));
        var completion = await factory.CreateClient().PostAsJsonAsync(
            "/api/v2/email-change/complete", new { token });
        Assert.Equal(HttpStatusCode.NoContent, completion.StatusCode);
    }

    [Fact]
    public async Task Expired_replacement_can_be_reclaimed_and_stale_owner_cannot_complete_or_resend()
    {
        var (owner, oldEmail) = await RegisterVerifiedAsync("EmailChangeExpiredOwner");
        var (contender, _) = await RegisterVerifiedAsync("EmailChangeExpiredContender");
        var target = $"reclaimed{Guid.NewGuid():N}@example.com";
        await RequestChangeAsync(owner.Client, target);
        var staleToken = factory.Services.GetRequiredService<CapturingEmailChangeDelivery>()
            .Verifications.Last(v => v.Destination == target).RawToken;
        await ExpireChallengeAsync(owner.Client);

        var reclaimed = await RequestChangeAsync(contender.Client, target.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.Accepted, reclaimed.StatusCode);
        var staleCompletion = await factory.CreateClient().PostAsJsonAsync(
            "/api/v2/email-change/complete", new { token = staleToken });
        Assert.Equal(HttpStatusCode.BadRequest, staleCompletion.StatusCode);
        Assert.Equal("invalid_email_change", await ReadCodeAsync(staleCompletion));

        var staleResend = await owner.Client.PostAsync("/api/v2/me/email/change/resend", null);
        Assert.Equal(HttpStatusCode.Conflict, staleResend.StatusCode);
        Assert.Equal("email_change_not_available", await ReadCodeAsync(staleResend));
        var ownerStatus = await owner.Client.GetFromJsonAsync<JsonElement>("/api/v2/me/email", JsonOptions);
        Assert.Equal(oldEmail, ownerStatus.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Initial_verification_can_reclaim_an_expired_replacement_reservation()
    {
        var (owner, _) = await RegisterVerifiedAsync("EciOwner");
        var newcomer = await factory.RegisterNewPlayerAsync("EciNew");
        var target = $"initialreclaim{Guid.NewGuid():N}@example.com";
        await RequestChangeAsync(owner.Client, target);
        await ExpireChallengeAsync(owner.Client);

        var response = await newcomer.Client.PostAsJsonAsync("/api/v2/me/email/verification", new
        {
            currentPassword = "P@ssw0rd123!",
            email = target.ToUpperInvariant()
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var status = await newcomer.Client.GetFromJsonAsync<JsonElement>("/api/v2/me/email", JsonOptions);
        Assert.Equal(target.ToUpperInvariant(), status.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Expired_initial_verification_target_is_not_reclaimed()
    {
        var owner = await factory.RegisterNewPlayerAsync("EieOwner");
        var contender = await factory.RegisterNewPlayerAsync("EieContender");
        var target = $"initialexpired{Guid.NewGuid():N}@example.com";
        var requested = await owner.Client.PostAsJsonAsync("/api/v2/me/email/verification", new
        {
            currentPassword = "P@ssw0rd123!",
            email = target
        });
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        await ExpireChallengeAsync(owner.Client);

        var conflict = await contender.Client.PostAsJsonAsync("/api/v2/me/email/verification", new
        {
            currentPassword = "P@ssw0rd123!",
            email = target.ToUpperInvariant()
        });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("email_unavailable", await ReadCodeAsync(conflict));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        var ownerId = await GetAccountIdAsync(owner.Client);
        var challenge = await db.EmailVerificationChallenges.AsNoTracking()
            .SingleAsync(c => c.AccountId == ownerId);
        Assert.Null(challenge.ConsumedAt);
    }

    [Fact]
    public async Task Concurrent_reclamation_of_expired_replacement_has_exactly_one_winner()
    {
        var (owner, _) = await RegisterVerifiedAsync("EcrOwner");
        var (first, _) = await RegisterVerifiedAsync("EcrFirst");
        var (second, _) = await RegisterVerifiedAsync("EcrSecond");
        var target = $"reclaimrace{Guid.NewGuid():N}@example.com";
        await RequestChangeAsync(owner.Client, target);
        await ExpireChallengeAsync(owner.Client);

        var responses = await Task.WhenAll(
            RequestChangeAsync(first.Client, target),
            RequestChangeAsync(second.Client, target.ToUpperInvariant()));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
        var conflict = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("email_unavailable", await ReadCodeAsync(conflict));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        Assert.Equal(1, await db.EmailVerificationChallenges.AsNoTracking().CountAsync(
            challenge => challenge.TargetEmailCanonical == target && challenge.ConsumedAt == null));
    }

    [Fact]
    public async Task Reclamation_and_stale_owner_resend_race_leave_exactly_one_active_reservation()
    {
        var (owner, _) = await RegisterVerifiedAsync("EcrrOwner");
        var (contender, _) = await RegisterVerifiedAsync("EcrrContender");
        var target = $"reclaimresend{Guid.NewGuid():N}@example.com";
        await RequestChangeAsync(owner.Client, target);
        await ExpireChallengeAsync(owner.Client);

        var responses = await Task.WhenAll(
            owner.Client.PostAsync("/api/v2/me/email/change/resend", null),
            RequestChangeAsync(contender.Client, target.ToUpperInvariant()));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
        Assert.All(
            responses.Where(response => response.StatusCode != HttpStatusCode.Accepted),
            response => Assert.Contains(
                response.StatusCode,
                new[] { HttpStatusCode.Conflict, HttpStatusCode.TooManyRequests }));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        Assert.Equal(1, await db.EmailVerificationChallenges.AsNoTracking().CountAsync(
            challenge => challenge.TargetEmailCanonical == target && challenge.ConsumedAt == null));
    }

    [Fact]
    public async Task Email_change_credential_cannot_complete_initial_verification_protocol()
    {
        var (player, _) = await RegisterVerifiedAsync("EmailChangeCrossProtocol");
        var target = $"crosschange{Guid.NewGuid():N}@example.com";
        await RequestChangeAsync(player.Client, target);
        var token = factory.Services.GetRequiredService<CapturingEmailChangeDelivery>()
            .Verifications.Last(v => v.Destination == target).RawToken;
        var anonymous = factory.CreateClient();

        var wrongProtocol = await anonymous.PostAsJsonAsync(
            "/api/v2/email-verification/complete", new { token });
        Assert.Equal(HttpStatusCode.BadRequest, wrongProtocol.StatusCode);
        Assert.Equal("invalid_email_verification", await ReadCodeAsync(wrongProtocol));

        var correctProtocol = await anonymous.PostAsJsonAsync(
            "/api/v2/email-change/complete", new { token });
        Assert.Equal(HttpStatusCode.NoContent, correctProtocol.StatusCode);
    }

    [Fact]
    public async Task Initial_verification_credential_cannot_complete_email_change_protocol()
    {
        var player = await factory.RegisterNewPlayerAsync("EmailInitialCrossProtocol");
        var target = $"crossinitial{Guid.NewGuid():N}@example.com";
        await player.Client.PostAsJsonAsync("/api/v2/me/email/verification", new
        {
            currentPassword = "P@ssw0rd123!",
            email = target
        });
        var token = factory.Services.GetRequiredService<CapturingEmailVerificationDelivery>()
            .Deliveries.Last(d => d.Destination == target).RawToken;
        var anonymous = factory.CreateClient();

        var wrongProtocol = await anonymous.PostAsJsonAsync(
            "/api/v2/email-change/complete", new { token });
        Assert.Equal(HttpStatusCode.BadRequest, wrongProtocol.StatusCode);
        Assert.Equal("invalid_email_change", await ReadCodeAsync(wrongProtocol));

        var correctProtocol = await anonymous.PostAsJsonAsync(
            "/api/v2/email-verification/complete", new { token });
        Assert.Equal(HttpStatusCode.NoContent, correctProtocol.StatusCode);
    }

    [Fact]
    public async Task Concurrent_completion_promotes_exactly_once()
    {
        var (player, _) = await RegisterVerifiedAsync("EmailChangeCompleteRace");
        var target = $"completerace{Guid.NewGuid():N}@example.com";
        await RequestChangeAsync(player.Client, target);
        var token = factory.Services.GetRequiredService<CapturingEmailChangeDelivery>()
            .Verifications.Last(v => v.Destination == target).RawToken;

        var responses = await Task.WhenAll(
            factory.CreateClient().PostAsJsonAsync("/api/v2/email-change/complete", new { token }),
            factory.CreateClient().PostAsJsonAsync("/api/v2/email-change/complete", new { token }));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
        var failure = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.BadRequest);
        Assert.Equal("invalid_email_change", await ReadCodeAsync(failure));
        var status = await player.Client.GetFromJsonAsync<JsonElement>("/api/v2/me/email", JsonOptions);
        Assert.Equal(target, status.GetProperty("email").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("pendingEmail").ValueKind);
    }

    [Fact]
    public async Task Cancellation_and_completion_race_has_only_a_consistent_terminal_state()
    {
        var (player, original) = await RegisterVerifiedAsync("EmailChangeCancelCompleteRace");
        var target = $"cancelcompleterace{Guid.NewGuid():N}@example.com";
        await RequestChangeAsync(player.Client, target);
        var token = factory.Services.GetRequiredService<CapturingEmailChangeDelivery>()
            .Verifications.Last(v => v.Destination == target).RawToken;

        var cancelTask = player.Client.DeleteAsync("/api/v2/me/email/change");
        var completeTask = factory.CreateClient().PostAsJsonAsync(
            "/api/v2/email-change/complete", new { token });
        await Task.WhenAll(cancelTask, completeTask);
        var cancel = await cancelTask;
        var complete = await completeTask;

        Assert.Contains(cancel.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict });
        Assert.Contains(complete.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.BadRequest });
        var status = await player.Client.GetFromJsonAsync<JsonElement>("/api/v2/me/email", JsonOptions);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("pendingEmail").ValueKind);
        Assert.Equal(
            complete.StatusCode == HttpStatusCode.NoContent ? target : original,
            status.GetProperty("email").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        var accountId = await GetAccountIdAsync(player.Client);
        var challenge = await db.EmailVerificationChallenges.AsNoTracking()
            .SingleAsync(c => c.AccountId == accountId);
        Assert.NotNull(challenge.ConsumedAt);
    }

    [Fact]
    public async Task Cancellation_does_not_bypass_persistent_delivery_cooldown()
    {
        var (player, _) = await RegisterVerifiedAsync("EmailChangeCancelCooldown");
        await RequestChangeAsync(player.Client, $"cancelcooldown{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.NoContent,
            (await player.Client.DeleteAsync("/api/v2/me/email/change")).StatusCode);

        var tooSoon = await RequestChangeAsync(
            player.Client, $"cancelcooldownnext{Guid.NewGuid():N}@example.com");

        Assert.Equal(HttpStatusCode.TooManyRequests, tooSoon.StatusCode);
        Assert.Equal("email_change_cooldown", await ReadCodeAsync(tooSoon));
    }

    private async Task<(RegisteredPlayer Player, string Email)> RegisterVerifiedAsync(string prefix)
    {
        var player = await factory.RegisterNewPlayerAsync(prefix);
        var email = $"{prefix.ToLowerInvariant()}{Guid.NewGuid():N}@example.com";
        var request = await player.Client.PostAsJsonAsync("/api/v2/me/email/verification", new
        {
            currentPassword = "P@ssw0rd123!",
            email
        });
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);

        var token = factory.Services.GetRequiredService<CapturingEmailVerificationDelivery>()
            .Deliveries.Last(d => d.Destination == email).RawToken;
        var complete = await factory.CreateClient().PostAsJsonAsync(
            "/api/v2/email-verification/complete", new { token });
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        return (player, email);
    }

    private Task<HttpResponseMessage> RequestChangeAsync(HttpClient client, string target)
        => client.PostAsJsonAsync("/api/v2/me/email/change", new
        {
            currentPassword = "P@ssw0rd123!",
            newEmail = target
        });

    private async Task BackdateChallengeAsync(HttpClient client, int minutes)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        var accountId = await GetAccountIdAsync(client);
        var challenge = await db.EmailVerificationChallenges.SingleAsync(c => c.AccountId == accountId);
        challenge.IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-minutes);
        await db.SaveChangesAsync();
    }

    private async Task ExpireChallengeAsync(HttpClient client)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        var accountId = await GetAccountIdAsync(client);
        var challenge = await db.EmailVerificationChallenges.SingleAsync(c => c.AccountId == accountId);
        challenge.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        challenge.IssuedAt = DateTimeOffset.UtcNow.AddHours(-25);
        await db.SaveChangesAsync();
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
