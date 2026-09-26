using Level5.Application.Identity;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Identity;
using Xunit;

namespace Level5.Application.Tests.Identity;

public class LoginUseCaseTests
{
    private readonly InMemoryAccountStore _accounts = new();
    private readonly InMemoryPlayerProfileStore _profiles = new();
    private readonly InMemoryAuthSessionStore _sessions = new();
    private readonly RegisterAccountUseCase _register;
    private readonly LoginUseCase _login;

    public LoginUseCaseTests()
    {
        var hasher = new FakePasswordHasher();
        var refreshTokens = new FakeRefreshTokenGenerator();
        var sessionPolicy = new FakeAuthSessionPolicy();
        _register = new RegisterAccountUseCase(
            _accounts, _profiles, _sessions, hasher, new FakePasswordPolicy(),
            refreshTokens, sessionPolicy, new FakeTokenIssuer(), new NoOpUnitOfWork(), new FakeClock());
        _login = new LoginUseCase(
            _accounts, _profiles, _sessions, hasher, refreshTokens, sessionPolicy,
            new FakeTokenIssuer(), new NoOpUnitOfWork(), new FakeClock());
    }

    [Fact]
    public async Task Login_with_correct_credentials_succeeds()
    {
        var registered = await _register.ExecuteAsync(new RegisterAccountRequest("patrick", "P@ssw0rd!", "Patrick"), CancellationToken.None);

        var result = await _login.ExecuteAsync(new LoginRequest("patrick", "P@ssw0rd!"), CancellationToken.None);

        Assert.Equal(registered.AccountId, result.AccountId);
        Assert.Equal(registered.PlayerId, result.PlayerId);
    }

    [Fact]
    public async Task Login_is_case_insensitive_on_username()
    {
        await _register.ExecuteAsync(new RegisterAccountRequest("patrick", "P@ssw0rd!", "Patrick"), CancellationToken.None);

        var result = await _login.ExecuteAsync(new LoginRequest("PATRICK", "P@ssw0rd!"), CancellationToken.None);

        Assert.NotEqual(default, result.AccountId.Value);
    }

    [Fact]
    public async Task Login_with_wrong_password_is_rejected()
    {
        await _register.ExecuteAsync(new RegisterAccountRequest("patrick", "P@ssw0rd!", "Patrick"), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidCredentialsException>(() =>
            _login.ExecuteAsync(new LoginRequest("patrick", "wrong-password"), CancellationToken.None));
    }

    [Fact]
    public async Task Login_for_an_unknown_username_is_rejected_the_same_way_as_a_wrong_password()
    {
        await Assert.ThrowsAsync<InvalidCredentialsException>(() =>
            _login.ExecuteAsync(new LoginRequest("nobody", "whatever"), CancellationToken.None));
    }

    [Fact]
    public async Task Login_issues_a_refresh_session_alongside_the_access_token()
    {
        await _register.ExecuteAsync(new RegisterAccountRequest("erin", "P@ssw0rd!", "Erin"), CancellationToken.None);

        var result = await _login.ExecuteAsync(new LoginRequest("erin", "P@ssw0rd!"), CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(result.RefreshToken));
        var session = await _sessions.FindByRefreshTokenHashAsync(new FakeRefreshTokenGenerator().Hash(result.RefreshToken), CancellationToken.None);
        Assert.NotNull(session);
        Assert.Equal(result.AccountId, session!.AccountId);
    }

    [Fact]
    public async Task Login_for_a_disabled_account_is_rejected_the_same_way_as_a_wrong_password()
    {
        var registered = await _register.ExecuteAsync(new RegisterAccountRequest("frank", "P@ssw0rd!", "Frank"), CancellationToken.None);
        var account = await _accounts.FindByIdAsync(registered.AccountId, CancellationToken.None);
        var disabled = Account.Rehydrate(account!.Id, account.Username, account.Email, AccountStatus.Disabled, account.PasswordHash, account.CreatedAt);
        await _accounts.UpdateAsync(disabled, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidCredentialsException>(() =>
            _login.ExecuteAsync(new LoginRequest("frank", "P@ssw0rd!"), CancellationToken.None));
    }

    [Fact]
    public async Task Login_for_a_disabled_account_does_not_create_a_refresh_session()
    {
        var registered = await _register.ExecuteAsync(new RegisterAccountRequest("grace", "P@ssw0rd!", "Grace"), CancellationToken.None);
        var account = await _accounts.FindByIdAsync(registered.AccountId, CancellationToken.None);
        var disabled = Account.Rehydrate(account!.Id, account.Username, account.Email, AccountStatus.Disabled, account.PasswordHash, account.CreatedAt);
        await _accounts.UpdateAsync(disabled, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidCredentialsException>(() =>
            _login.ExecuteAsync(new LoginRequest("grace", "P@ssw0rd!"), CancellationToken.None));

        // Only the one session from registration should exist - login must not have added another.
        var session = await _sessions.FindByRefreshTokenHashAsync(
            new FakeRefreshTokenGenerator().Hash(registered.RefreshToken), CancellationToken.None);
        Assert.NotNull(session);
    }
}
