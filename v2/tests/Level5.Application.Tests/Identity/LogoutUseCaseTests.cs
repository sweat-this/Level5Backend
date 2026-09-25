using Level5.Application.Identity;
using Level5.Application.Players;
using Level5.Application.Tests.Fakes;
using Xunit;

namespace Level5.Application.Tests.Identity;

public class LogoutUseCaseTests
{
    private readonly InMemoryAccountStore _accounts = new();
    private readonly InMemoryPlayerProfileStore _profiles = new();
    private readonly InMemoryAuthSessionStore _sessions = new();
    private readonly FakeRefreshTokenGenerator _refreshTokens = new();
    private readonly FakeClock _clock = new();
    private readonly RegisterAccountUseCase _register;
    private readonly RefreshSessionUseCase _refresh;
    private readonly LogoutUseCase _logout;

    public LogoutUseCaseTests()
    {
        var sessionPolicy = new FakeAuthSessionPolicy();
        _register = new RegisterAccountUseCase(
            _accounts, _profiles, _sessions, new FakePasswordHasher(), new FakePasswordPolicy(),
            _refreshTokens, sessionPolicy, new FakeTokenIssuer(), new NoOpUnitOfWork(), _clock, new PlayerTagAllocator(_profiles));
        _refresh = new RefreshSessionUseCase(
            _sessions, _accounts, _profiles, _refreshTokens, sessionPolicy, new FakeTokenIssuer(), _clock);
        _logout = new LogoutUseCase(_sessions, _refreshTokens, _clock);
    }

    [Fact]
    public async Task Logout_revokes_the_session_so_it_can_no_longer_refresh()
    {
        var registered = await _register.ExecuteAsync(new RegisterAccountRequest("alice", "P@ssw0rd!", "Alice"), CancellationToken.None);

        await _logout.ExecuteAsync(new LogoutRequest(registered.RefreshToken), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() =>
            _refresh.ExecuteAsync(new RefreshSessionRequest(registered.RefreshToken), CancellationToken.None));
    }

    [Fact]
    public async Task Repeated_logout_with_the_same_token_is_a_safe_no_op()
    {
        var registered = await _register.ExecuteAsync(new RegisterAccountRequest("bob", "P@ssw0rd!", "Bob"), CancellationToken.None);

        await _logout.ExecuteAsync(new LogoutRequest(registered.RefreshToken), CancellationToken.None);
        await _logout.ExecuteAsync(new LogoutRequest(registered.RefreshToken), CancellationToken.None);
    }

    [Fact]
    public async Task Logout_with_an_unknown_token_is_a_safe_no_op()
    {
        await _logout.ExecuteAsync(new LogoutRequest("not-a-real-token"), CancellationToken.None);
    }

    [Fact]
    public async Task Logout_after_a_rotation_still_revokes_using_the_current_token()
    {
        var registered = await _register.ExecuteAsync(new RegisterAccountRequest("carol", "P@ssw0rd!", "Carol"), CancellationToken.None);
        var rotated = await _refresh.ExecuteAsync(new RefreshSessionRequest(registered.RefreshToken), CancellationToken.None);

        await _logout.ExecuteAsync(new LogoutRequest(rotated.RefreshToken), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() =>
            _refresh.ExecuteAsync(new RefreshSessionRequest(rotated.RefreshToken), CancellationToken.None));
    }

    [Fact]
    public async Task A_lookup_failure_propagates_instead_of_being_swallowed_into_a_successful_no_op()
    {
        var useCase = new LogoutUseCase(new ThrowingAuthSessionStore(), _refreshTokens, _clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            useCase.ExecuteAsync(new LogoutRequest("whatever"), CancellationToken.None));
    }

    [Fact]
    public async Task A_save_failure_during_revocation_propagates_instead_of_being_swallowed_into_a_successful_no_op()
    {
        var registered = await _register.ExecuteAsync(new RegisterAccountRequest("dan", "P@ssw0rd!", "Dan"), CancellationToken.None);
        var useCase = new LogoutUseCase(new ThrowingOnSaveAuthSessionStore(_sessions), _refreshTokens, _clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            useCase.ExecuteAsync(new LogoutRequest(registered.RefreshToken), CancellationToken.None));
    }
}
