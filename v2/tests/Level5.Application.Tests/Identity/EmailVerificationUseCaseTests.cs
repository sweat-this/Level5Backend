using Level5.Application.Identity;
using Level5.Application.Common;
using Level5.Application.Abstractions;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Xunit;

namespace Level5.Application.Tests.Identity;

public sealed class EmailVerificationUseCaseTests
{
    private const string Password = "P@ssw0rd123!";
    private readonly FakeClock _clock = new();
    private readonly InMemoryAccountStore _accounts = new();
    private readonly InMemoryEmailVerificationChallengeStore _challenges = new();
    private readonly FakePasswordHasher _passwordHasher = new();
    private readonly FakeEmailVerificationTokenGenerator _tokens = new();
    private readonly FakeEmailVerificationPolicy _policy = new();
    private readonly FakeEmailVerificationDelivery _delivery = new();
    private readonly TrackingUnitOfWork _unitOfWork = new();

    [Fact]
    public async Task Request_requires_the_current_password_and_changes_nothing_when_it_is_wrong()
    {
        var account = await AddAccountAsync();

        await Assert.ThrowsAsync<CurrentPasswordInvalidException>(() =>
            RequestUseCase().ExecuteAsync(
                new(account.Id, "wrong", "target@example.com"), CancellationToken.None));

        Assert.Null(account.Email);
        Assert.Empty(_delivery.Deliveries);
        Assert.Null(await _challenges.FindByAccountIdAsync(account.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Request_attaches_an_unverified_email_commits_then_delivers()
    {
        var account = await AddAccountAsync();
        _delivery.OnDelivery = () => Assert.True(_unitOfWork.Saved);

        var result = await RequestUseCase().ExecuteAsync(
            new(account.Id, Password, "Target@Example.com"), CancellationToken.None);

        Assert.False(result.AlreadyVerified);
        Assert.Equal("Target@Example.com", account.Email!.Value);
        Assert.Null(account.EmailVerifiedAt);
        var challenge = await _challenges.FindByAccountIdAsync(account.Id, CancellationToken.None);
        Assert.NotNull(challenge);
        Assert.Single(_delivery.Deliveries);
        Assert.NotEqual(_delivery.Deliveries[0].RawToken, challenge!.TokenHash);
    }

    [Fact]
    public async Task Request_can_replace_an_unverified_target_and_rotates_the_credential()
    {
        var account = await AddAccountAsync(Email.Create("first@example.com"));
        await RequestUseCase().ExecuteAsync(
            new(account.Id, Password, "first@example.com"), CancellationToken.None);
        var first = await _challenges.FindByAccountIdAsync(account.Id, CancellationToken.None);

        _clock.UtcNow += _policy.ResendCooldown;
        await RequestUseCase().ExecuteAsync(
            new(account.Id, Password, "second@example.com"), CancellationToken.None);
        var second = await _challenges.FindByAccountIdAsync(account.Id, CancellationToken.None);

        Assert.Equal("second@example.com", account.Email!.Value);
        Assert.NotEqual(first!.TokenHash, second!.TokenHash);
        Assert.Equal(1, second.Revision);
    }

    [Fact]
    public async Task Request_enforces_the_persistent_account_cooldown_before_mutating_or_delivering()
    {
        var account = await AddAccountAsync();
        await RequestUseCase().ExecuteAsync(
            new(account.Id, Password, "first@example.com"), CancellationToken.None);
        var first = await _challenges.FindByAccountIdAsync(account.Id, CancellationToken.None);

        await Assert.ThrowsAsync<EmailVerificationCooldownException>(() =>
            RequestUseCase().ExecuteAsync(
                new(account.Id, Password, "first@example.com"), CancellationToken.None));
        await Assert.ThrowsAsync<EmailVerificationCooldownException>(() =>
            RequestUseCase().ExecuteAsync(
                new(account.Id, Password, "replacement@example.com"), CancellationToken.None));

        var unchanged = await _challenges.FindByAccountIdAsync(account.Id, CancellationToken.None);
        Assert.Equal("first@example.com", account.Email!.Value);
        Assert.Equal(first!.TokenHash, unchanged!.TokenHash);
        Assert.Equal(first.Revision, unchanged.Revision);
        Assert.Single(_delivery.Deliveries);
    }

    [Fact]
    public async Task Request_same_verified_target_is_no_op_and_different_target_is_deferred()
    {
        var email = Email.Create("verified@example.com");
        var account = Account.Rehydrate(
            AccountId.New(), Username.Create("verified"), email, AccountStatus.Active,
            _passwordHasher.Hash(Password), _clock.UtcNow, _clock.UtcNow);
        await _accounts.AddAsync(account, CancellationToken.None);

        var result = await RequestUseCase().ExecuteAsync(
            new(account.Id, Password, "VERIFIED@example.com"), CancellationToken.None);
        Assert.True(result.AlreadyVerified);
        Assert.Empty(_delivery.Deliveries);

        await Assert.ThrowsAsync<VerifiedEmailChangeNotAllowedException>(() =>
            RequestUseCase().ExecuteAsync(
                new(account.Id, Password, "other@example.com"), CancellationToken.None));
    }

    [Fact]
    public async Task Disabled_account_cannot_request_resend_or_complete()
    {
        var email = Email.Create("disabled@example.com");
        var account = await AddAccountAsync(email, AccountStatus.Disabled);

        await Assert.ThrowsAsync<AccountSecurityActionForbiddenException>(() =>
            RequestUseCase().ExecuteAsync(
                new(account.Id, Password, email.Value), CancellationToken.None));
        await Assert.ThrowsAsync<AccountSecurityActionForbiddenException>(() =>
            ResendUseCase().ExecuteAsync(account.Id, CancellationToken.None));

        var token = _tokens.Generate();
        await _challenges.AddAsync(EmailVerificationChallenge.Create(
            account.Id, email, token.Hash, _clock.UtcNow, _policy.TokenLifetime), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidEmailVerificationException>(() =>
            CompleteUseCase().ExecuteAsync(token.RawValue, CancellationToken.None));
    }

    [Fact]
    public async Task Delivery_failure_leaves_a_recoverable_unverified_challenge()
    {
        var account = await AddAccountAsync();
        _delivery.ThrowOnDelivery = true;

        await Assert.ThrowsAsync<EmailVerificationDeliveryUnavailableException>(() =>
            RequestUseCase().ExecuteAsync(
                new(account.Id, Password, "target@example.com"), CancellationToken.None));

        Assert.Equal("target@example.com", account.Email!.Value);
        Assert.Null(account.EmailVerifiedAt);
        Assert.NotNull(await _challenges.FindByAccountIdAsync(account.Id, CancellationToken.None));
        Assert.True(_unitOfWork.Saved);
    }

    [Fact]
    public async Task Resend_enforces_persistent_cooldown_then_rotates_after_it()
    {
        var account = await AddAccountAsync();
        await RequestUseCase().ExecuteAsync(
            new(account.Id, Password, "target@example.com"), CancellationToken.None);
        var first = await _challenges.FindByAccountIdAsync(account.Id, CancellationToken.None);

        await Assert.ThrowsAsync<EmailVerificationCooldownException>(() =>
            ResendUseCase().ExecuteAsync(account.Id, CancellationToken.None));

        _clock.UtcNow += _policy.ResendCooldown;
        await ResendUseCase().ExecuteAsync(account.Id, CancellationToken.None);
        var second = await _challenges.FindByAccountIdAsync(account.Id, CancellationToken.None);

        Assert.NotEqual(first!.TokenHash, second!.TokenHash);
        Assert.Equal(1, second.Revision);
    }

    [Fact]
    public async Task Complete_verifies_and_consumes_then_rejects_replay_with_generic_failure()
    {
        var account = await AddAccountAsync();
        await RequestUseCase().ExecuteAsync(
            new(account.Id, Password, "target@example.com"), CancellationToken.None);
        var rawToken = _delivery.Deliveries.Single().RawToken;

        await CompleteUseCase().ExecuteAsync(rawToken, CancellationToken.None);

        Assert.Equal(_clock.UtcNow, account.EmailVerifiedAt);
        var consumed = await _challenges.FindByAccountIdAsync(account.Id, CancellationToken.None);
        Assert.Equal(_clock.UtcNow, consumed!.ConsumedAt);
        await Assert.ThrowsAsync<InvalidEmailVerificationException>(() =>
            CompleteUseCase().ExecuteAsync(rawToken, CancellationToken.None));
    }

    [Fact]
    public async Task Complete_rejects_expired_and_email_mismatch_with_same_generic_failure()
    {
        var account = await AddAccountAsync();
        await RequestUseCase().ExecuteAsync(
            new(account.Id, Password, "target@example.com"), CancellationToken.None);
        var rawToken = _delivery.Deliveries.Single().RawToken;

        _clock.UtcNow += _policy.TokenLifetime;
        await Assert.ThrowsAsync<InvalidEmailVerificationException>(() =>
            CompleteUseCase().ExecuteAsync(rawToken, CancellationToken.None));

        _clock.UtcNow -= _policy.TokenLifetime;
        account.AttachOrReplaceUnverifiedEmail(Email.Create("replacement@example.com"));
        await Assert.ThrowsAsync<InvalidEmailVerificationException>(() =>
            CompleteUseCase().ExecuteAsync(rawToken, CancellationToken.None));
        Assert.Null(account.EmailVerifiedAt);
    }

    [Fact]
    public async Task Concurrent_persistence_losers_map_to_operation_specific_safe_outcomes()
    {
        var account = await AddAccountAsync(Email.Create("race@example.com"));
        var token = _tokens.Generate();
        await _challenges.AddAsync(EmailVerificationChallenge.Create(
            account.Id, account.Email!, token.Hash, _clock.UtcNow.AddMinutes(-10), _policy.TokenLifetime),
            CancellationToken.None);
        var conflict = new ConflictUnitOfWork();

        var resend = new ResendEmailVerificationUseCase(
            _accounts, _challenges, _tokens, _policy, _delivery, conflict, _clock);
        await Assert.ThrowsAsync<EmailVerificationCooldownException>(() =>
            resend.ExecuteAsync(account.Id, CancellationToken.None));

        var completionToken = _tokens.Generate();
        await _challenges.AddAsync(EmailVerificationChallenge.Create(
            account.Id, account.Email!, completionToken.Hash, _clock.UtcNow, _policy.TokenLifetime),
            CancellationToken.None);

        var complete = new CompleteEmailVerificationUseCase(
            _challenges, _tokens, _accounts, conflict, _clock);
        await Assert.ThrowsAsync<InvalidEmailVerificationException>(() =>
            complete.ExecuteAsync(completionToken.RawValue, CancellationToken.None));
    }

    private RequestEmailVerificationUseCase RequestUseCase() => new(
        _accounts, _challenges, _passwordHasher, _tokens, _policy,
        _delivery, _unitOfWork, _clock);

    private ResendEmailVerificationUseCase ResendUseCase() => new(
        _accounts, _challenges, _tokens, _policy, _delivery, _unitOfWork, _clock);

    private CompleteEmailVerificationUseCase CompleteUseCase() => new(
        _challenges, _tokens, _accounts, _unitOfWork, _clock);

    private async Task<Account> AddAccountAsync(
        Email? email = null,
        AccountStatus status = AccountStatus.Active)
    {
        var account = Account.Rehydrate(
            AccountId.New(), Username.Create($"u{Guid.NewGuid():N}"[..15]), email, status,
            _passwordHasher.Hash(Password), _clock.UtcNow);
        await _accounts.AddAsync(account, CancellationToken.None);
        return account;
    }

    private sealed class ConflictUnitOfWork : IUnitOfWork
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken)
            => throw new ConflictException("Synthetic optimistic-concurrency loser.");
    }
}
