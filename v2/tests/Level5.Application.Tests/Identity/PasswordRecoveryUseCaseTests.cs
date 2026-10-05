using Level5.Application.Abstractions;
using Level5.Application.Identity;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Xunit;

namespace Level5.Application.Tests.Identity;

public sealed class PasswordRecoveryUseCaseTests
{
    private readonly FakeClock _clock = new();
    private readonly InMemoryAccountStore _accounts = new();
    private readonly InMemoryPasswordResetChallengeStore _challenges = new();
    private readonly FakePasswordResetTokenGenerator _tokens = new();
    private readonly FakePasswordRecoveryPolicy _policy = new();
    private readonly FakePasswordRecoveryDelivery _delivery = new();
    private readonly FakePasswordHasher _hasher = new();
    private readonly FakePasswordPolicy _passwordPolicy = new();

    [Fact]
    public async Task Unknown_unverified_and_disabled_accounts_do_not_dispatch_or_fail()
    {
        var request = CreateRequestUseCase();
        await request.ExecuteAsync(new("unknown@example.com"), default);

        await _accounts.AddAsync(Account.Register(Username.Create("unverified"), "hash", _clock.UtcNow, Email.Create("unverified@example.com")), default);
        await request.ExecuteAsync(new("unverified@example.com"), default);

        var disabled = VerifiedAccount("disabled", "disabled@example.com", AccountStatus.Disabled);
        await _accounts.AddAsync(disabled, default);
        await request.ExecuteAsync(new("disabled@example.com"), default);

        Assert.Empty(_delivery.Deliveries);
    }

    [Fact]
    public async Task Eligible_request_dispatches_once_and_cooldown_is_unobservable()
    {
        var account = VerifiedAccount("active", "active@example.com");
        await _accounts.AddAsync(account, default);
        var useCase = CreateRequestUseCase();

        await useCase.ExecuteAsync(new("ACTIVE@example.com"), default);
        await useCase.ExecuteAsync(new("active@example.com"), default);

        Assert.Single(_delivery.Deliveries);
        Assert.Equal("active@example.com", _delivery.Deliveries[0].Destination.Canonical);
    }

    [Fact]
    public async Task Delivery_unavailable_does_not_fail_request()
    {
        await _accounts.AddAsync(VerifiedAccount("active", "active@example.com"), default);
        _delivery.Outcome = PasswordRecoveryDeliveryOutcome.Unavailable;
        await CreateRequestUseCase().ExecuteAsync(new("active@example.com"), default);
        Assert.Single(_delivery.Deliveries);
    }

    [Fact]
    public async Task Delivery_provider_fault_does_not_reveal_an_eligible_account()
    {
        await _accounts.AddAsync(VerifiedAccount("active", "active@example.com"), default);
        var delivery = new ThrowingPasswordRecoveryDelivery();
        var useCase = new RequestPasswordResetUseCase(
            _accounts, _challenges, _tokens, _policy, delivery, new NoOpUnitOfWork(), _clock);

        await useCase.ExecuteAsync(new("active@example.com"), default);

        Assert.Equal(1, delivery.Attempts);
    }

    [Fact]
    public async Task Reset_is_single_use_and_increments_session_generation()
    {
        var account = VerifiedAccount("active", "active@example.com");
        await _accounts.AddAsync(account, default);
        await CreateRequestUseCase().ExecuteAsync(new("active@example.com"), default);
        var rawToken = _delivery.Deliveries.Single().RawToken;
        var useCase = CreateCompleteUseCase();

        await useCase.ExecuteAsync(new(rawToken, "new-password"), default);

        Assert.Equal(1, account.SessionGeneration);
        Assert.Equal("HASHED:new-password", account.PasswordHash);
        await Assert.ThrowsAsync<InvalidPasswordResetException>(() => useCase.ExecuteAsync(new(rawToken, "another"), default));
    }

    [Fact]
    public async Task Reset_rejects_target_email_mismatch()
    {
        var account = VerifiedAccount("active", "active@example.com");
        await _accounts.AddAsync(account, default);
        await CreateRequestUseCase().ExecuteAsync(new("active@example.com"), default);
        var rawToken = _delivery.Deliveries.Single().RawToken;
        var changed = Account.Rehydrate(account.Id, account.Username, Email.Create("other@example.com"), AccountStatus.Active,
            account.PasswordHash, account.CreatedAt, _clock.UtcNow, account.SessionGeneration);
        await _accounts.StageEmailUpdateAsync(changed, default);

        await Assert.ThrowsAsync<InvalidPasswordResetException>(() => CreateCompleteUseCase().ExecuteAsync(new(rawToken, "new-password"), default));
    }

    [Fact]
    public async Task Authenticated_change_requires_current_password_and_increments_generation_only_on_success()
    {
        var account = VerifiedAccount("active", "active@example.com", passwordHash: _hasher.Hash("current"));
        await _accounts.AddAsync(account, default);
        var useCase = new ChangePasswordUseCase(_accounts, _hasher, _passwordPolicy, new NoOpUnitOfWork());

        await Assert.ThrowsAsync<CurrentPasswordInvalidException>(() => useCase.ExecuteAsync(new(account.Id, "wrong", "new"), default));
        Assert.Equal(0, account.SessionGeneration);

        await useCase.ExecuteAsync(new(account.Id, "current", "new"), default);
        Assert.Equal(1, account.SessionGeneration);
    }

    private RequestPasswordResetUseCase CreateRequestUseCase() => new(
        _accounts, _challenges, _tokens, _policy, _delivery, new NoOpUnitOfWork(), _clock);

    private CompletePasswordResetUseCase CreateCompleteUseCase() => new(
        _challenges, _tokens, _accounts, _hasher, _passwordPolicy, new NoOpUnitOfWork(), _clock);

    private Account VerifiedAccount(string username, string email, AccountStatus status = AccountStatus.Active, string passwordHash = "hash")
        => Account.Rehydrate(AccountId.New(), Username.Create(username), Email.Create(email), status, passwordHash,
            _clock.UtcNow, _clock.UtcNow);

    private sealed class ThrowingPasswordRecoveryDelivery : IPasswordRecoveryDelivery
    {
        public int Attempts { get; private set; }

        public Task<PasswordRecoveryDeliveryOutcome> DeliverAsync(
            Email destination,
            string rawToken,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken)
        {
            Attempts++;
            throw new InvalidOperationException("Simulated provider failure.");
        }
    }
}
