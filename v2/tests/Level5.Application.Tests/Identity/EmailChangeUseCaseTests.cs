using Level5.Application.Identity;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Xunit;

namespace Level5.Application.Tests.Identity;

public sealed class EmailChangeUseCaseTests
{
    private readonly FakeClock _clock = new();
    private readonly InMemoryAccountStore _accounts = new();
    private readonly InMemoryEmailVerificationChallengeStore _challenges = new();
    private readonly FakePasswordHasher _passwords = new();
    private readonly FakeEmailVerificationTokenGenerator _tokens = new();
    private readonly FakeEmailVerificationPolicy _policy = new();
    private readonly FakeEmailChangeDelivery _delivery = new();
    private readonly TrackingUnitOfWork _uow = new();

    [Fact]
    public async Task Request_keeps_verified_email_canonical_and_creates_pending_replacement()
    {
        var account = await AddVerifiedAsync("old@example.com");

        var result = await Request().ExecuteAsync(
            new(account.Id, "current-password", "new@example.com"), CancellationToken.None);

        Assert.Equal("old@example.com", account.Email!.Value);
        Assert.NotNull(account.EmailVerifiedAt);
        Assert.Equal("new@example.com", result.PendingEmail);
        Assert.Single(_delivery.Verifications);
        var challenge = await _challenges.FindByAccountIdAsync(account.Id, CancellationToken.None);
        Assert.Equal("new@example.com", challenge!.TargetEmail.Value);
    }

    [Fact]
    public async Task Request_requires_current_password()
    {
        var account = await AddVerifiedAsync("old2@example.com");

        await Assert.ThrowsAsync<CurrentPasswordInvalidException>(() =>
            Request().ExecuteAsync(new(account.Id, "wrong", "new2@example.com"), CancellationToken.None));

        Assert.Empty(_delivery.Verifications);
    }

    [Fact]
    public async Task Request_requires_an_active_account_with_a_verified_email()
    {
        var unverified = Account.Register(
            Username.Create($"u{Guid.NewGuid():N}"[..15]),
            _passwords.Hash("current-password"),
            _clock.UtcNow,
            Email.Create("unverified@example.com"));
        await _accounts.AddAsync(unverified, CancellationToken.None);
        var disabled = Account.Rehydrate(
            AccountId.New(),
            Username.Create($"u{Guid.NewGuid():N}"[..15]),
            Email.Create("disabled@example.com"),
            AccountStatus.Disabled,
            _passwords.Hash("current-password"),
            _clock.UtcNow,
            _clock.UtcNow);
        await _accounts.AddAsync(disabled, CancellationToken.None);

        await Assert.ThrowsAsync<EmailChangeNotAvailableException>(() => Request().ExecuteAsync(
            new(unverified.Id, "current-password", "new-unverified@example.com"), CancellationToken.None));
        await Assert.ThrowsAsync<EmailChangeNotAvailableException>(() => Request().ExecuteAsync(
            new(disabled.Id, "current-password", "new-disabled@example.com"), CancellationToken.None));
    }

    [Fact]
    public async Task Request_rejects_current_or_another_accounts_canonical_email()
    {
        var account = await AddVerifiedAsync("current@example.com");
        await AddVerifiedAsync("owned@example.com");

        await Assert.ThrowsAsync<EmailAlreadyCurrentException>(() => Request().ExecuteAsync(
            new(account.Id, "current-password", "CURRENT@example.com"), CancellationToken.None));
        await Assert.ThrowsAsync<EmailUnavailableException>(() => Request().ExecuteAsync(
            new(account.Id, "current-password", "OWNED@example.com"), CancellationToken.None));
    }

    [Fact]
    public async Task Verification_delivery_failure_leaves_the_committed_pending_change_recoverable()
    {
        var account = await AddVerifiedAsync("delivery-old@example.com");
        _delivery.ThrowOnVerification = true;

        await Assert.ThrowsAsync<EmailChangeDeliveryUnavailableException>(() => Request().ExecuteAsync(
            new(account.Id, "current-password", "delivery-new@example.com"), CancellationToken.None));

        Assert.True(_uow.Saved);
        var challenge = await _challenges.FindByAccountIdAsync(account.Id, CancellationToken.None);
        Assert.NotNull(challenge);
        Assert.Null(challenge!.ConsumedAt);
        Assert.Equal("delivery-new@example.com", challenge.TargetEmail.Value);
        Assert.Equal("delivery-old@example.com", account.Email!.Value);
    }

    [Fact]
    public async Task Resend_rotates_the_token_without_changing_target()
    {
        var account = await AddVerifiedAsync("old3@example.com");
        await Request().ExecuteAsync(
            new(account.Id, "current-password", "new3@example.com"), CancellationToken.None);
        var first = Assert.Single(_delivery.Verifications).RawToken;
        _clock.UtcNow = _clock.UtcNow.AddMinutes(6);

        await Resend().ExecuteAsync(account.Id, CancellationToken.None);

        Assert.Equal(2, _delivery.Verifications.Count);
        Assert.NotEqual(first, _delivery.Verifications.Last().RawToken);
        var challenge = await _challenges.FindByAccountIdAsync(account.Id, CancellationToken.None);
        Assert.Equal("new3@example.com", challenge!.TargetEmail.Value);
    }

    [Fact]
    public async Task Cancel_invalidates_pending_change_and_preserves_current_email()
    {
        var account = await AddVerifiedAsync("old4@example.com");
        await Request().ExecuteAsync(
            new(account.Id, "current-password", "new4@example.com"), CancellationToken.None);

        await new CancelEmailChangeUseCase(_accounts, _challenges, _uow, _clock)
            .ExecuteAsync(account.Id, CancellationToken.None);

        var challenge = await _challenges.FindByAccountIdAsync(account.Id, CancellationToken.None);
        Assert.NotNull(challenge!.ConsumedAt);
        Assert.Equal("old4@example.com", account.Email!.Value);
    }

    [Fact]
    public async Task Completion_promotes_replacement_once_and_notifies_previous_address()
    {
        var account = await AddVerifiedAsync("old5@example.com");
        await Request().ExecuteAsync(
            new(account.Id, "current-password", "new5@example.com"), CancellationToken.None);
        var rawToken = Assert.Single(_delivery.Verifications).RawToken;

        await Complete().ExecuteAsync(rawToken, CancellationToken.None);

        Assert.Equal("new5@example.com", account.Email!.Value);
        Assert.Equal(_clock.UtcNow, account.EmailVerifiedAt);
        Assert.Equal(0, account.SessionGeneration);
        Assert.Equal("old5@example.com", Assert.Single(_delivery.PreviousAddressNotifications).Value);

        await Assert.ThrowsAsync<InvalidEmailChangeException>(() =>
            Complete().ExecuteAsync(rawToken, CancellationToken.None));
    }

    [Fact]
    public async Task Previous_address_notification_failure_does_not_undo_completed_change()
    {
        var account = await AddVerifiedAsync("old6@example.com");
        await Request().ExecuteAsync(
            new(account.Id, "current-password", "new6@example.com"), CancellationToken.None);
        var rawToken = Assert.Single(_delivery.Verifications).RawToken;
        _delivery.ThrowOnPreviousAddressNotification = true;

        await Complete().ExecuteAsync(rawToken, CancellationToken.None);

        Assert.Equal("new6@example.com", account.Email!.Value);
    }

    private RequestEmailChangeUseCase Request() => new(
        _accounts, _challenges, _passwords, _tokens, _policy, _delivery, _uow, _clock);

    private ResendEmailChangeUseCase Resend() => new(
        _accounts, _challenges, _tokens, _policy, _delivery, _uow, _clock);

    private CompleteEmailChangeUseCase Complete() => new(
        _challenges, _tokens, _accounts, _delivery, _uow, _clock);

    private async Task<Account> AddVerifiedAsync(string email)
    {
        var account = Account.Register(
            Username.Create($"u{Guid.NewGuid():N}"[..15]),
            _passwords.Hash("current-password"),
            _clock.UtcNow,
            Email.Create(email));
        account.VerifyEmail(account.Email!, _clock.UtcNow.AddMinutes(-1));
        await _accounts.AddAsync(account, CancellationToken.None);
        return account;
    }
}
