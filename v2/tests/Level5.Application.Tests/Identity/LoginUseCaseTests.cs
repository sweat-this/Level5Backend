using Level5.Application.Identity;
using Level5.Application.Tests.Fakes;
using Xunit;

namespace Level5.Application.Tests.Identity;

public class LoginUseCaseTests
{
    private readonly InMemoryAccountStore _accounts = new();
    private readonly InMemoryPlayerProfileStore _profiles = new();
    private readonly RegisterAccountUseCase _register;
    private readonly LoginUseCase _login;

    public LoginUseCaseTests()
    {
        var hasher = new FakePasswordHasher();
        _register = new RegisterAccountUseCase(_accounts, _profiles, hasher, new FakeTokenIssuer(), new NoOpUnitOfWork(), new FakeClock());
        _login = new LoginUseCase(_accounts, _profiles, hasher, new FakeTokenIssuer(), new NoOpUnitOfWork());
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
}
