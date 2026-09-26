using Level5.Application.Identity;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Identity;
using Xunit;

namespace Level5.Application.Tests.Identity;

public class RefreshSessionUseCaseTests
{
    private readonly InMemoryAccountStore _accounts = new();
    private readonly InMemoryPlayerProfileStore _profiles = new();
    private readonly InMemoryAuthSessionStore _sessions = new();
    private readonly FakeRefreshTokenGenerator _refreshTokens = new();
    private readonly FakeClock _clock = new();
    private readonly RegisterAccountUseCase _register;
    private readonly RefreshSessionUseCase _refresh;

    public RefreshSessionUseCaseTests()
    {
        var sessionPolicy = new FakeAuthSessionPolicy();
        _register = new RegisterAccountUseCase(
            _accounts, _profiles, _sessions, new FakePasswordHasher(), new FakePasswordPolicy(),
            _refreshTokens, sessionPolicy, new FakeTokenIssuer(), new NoOpUnitOfWork(), _clock);
        _refresh = new RefreshSessionUseCase(
            _sessions, _accounts, _profiles, _refreshTokens, sessionPolicy, new FakeTokenIssuer(), _clock);
    }

    [Fact]
    public async Task Refreshing_a_valid_token_returns_a_new_access_and_refresh_token()
    {
        var registered = await _register.ExecuteAsync(new RegisterAccountRequest("alice", "P@ssw0rd!", "Alice"), CancellationToken.None);

        var result = await _refresh.ExecuteAsync(new RefreshSessionRequest(registered.RefreshToken), CancellationToken.None);

        Assert.Equal(registered.AccountId, result.AccountId);
        Assert.Equal(registered.PlayerId, result.PlayerId);
        Assert.NotEqual(registered.RefreshToken, result.RefreshToken);
    }

    [Fact]
    public async Task The_old_refresh_token_cannot_be_reused_after_a_successful_refresh()
    {
        var registered = await _register.ExecuteAsync(new RegisterAccountRequest("bob", "P@ssw0rd!", "Bob"), CancellationToken.None);
        await _refresh.ExecuteAsync(new RefreshSessionRequest(registered.RefreshToken), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() =>
            _refresh.ExecuteAsync(new RefreshSessionRequest(registered.RefreshToken), CancellationToken.None));
    }

    [Fact]
    public async Task The_new_refresh_token_from_a_rotation_works()
    {
        var registered = await _register.ExecuteAsync(new RegisterAccountRequest("carol", "P@ssw0rd!", "Carol"), CancellationToken.None);
        var first = await _refresh.ExecuteAsync(new RefreshSessionRequest(registered.RefreshToken), CancellationToken.None);

        var second = await _refresh.ExecuteAsync(new RefreshSessionRequest(first.RefreshToken), CancellationToken.None);

        Assert.Equal(registered.AccountId, second.AccountId);
    }

    [Fact]
    public async Task An_unknown_refresh_token_is_rejected()
    {
        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() =>
            _refresh.ExecuteAsync(new RefreshSessionRequest("not-a-real-token"), CancellationToken.None));
    }

    [Fact]
    public async Task An_expired_refresh_token_is_rejected()
    {
        var registered = await _register.ExecuteAsync(new RegisterAccountRequest("dave", "P@ssw0rd!", "Dave"), CancellationToken.None);
        _clock.UtcNow = registered.RefreshTokenExpiresAt.AddSeconds(1);

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() =>
            _refresh.ExecuteAsync(new RefreshSessionRequest(registered.RefreshToken), CancellationToken.None));
    }

    [Fact]
    public async Task A_refresh_token_for_a_disabled_account_is_rejected()
    {
        var registered = await _register.ExecuteAsync(new RegisterAccountRequest("erin", "P@ssw0rd!", "Erin"), CancellationToken.None);
        var account = await _accounts.FindByIdAsync(registered.AccountId, CancellationToken.None);
        var disabled = Account.Rehydrate(account!.Id, account.Username, account.Email, AccountStatus.Disabled, account.PasswordHash, account.CreatedAt);
        await _accounts.UpdateAsync(disabled, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() =>
            _refresh.ExecuteAsync(new RefreshSessionRequest(registered.RefreshToken), CancellationToken.None));
    }

    [Fact]
    public async Task A_stale_revision_write_is_rejected_by_the_store_so_only_one_rotation_of_a_given_load_can_win()
    {
        // Two "concurrent" requests both load the same session at the same revision (as they
        // would if they raced in real usage) before either writes back. This exercises the
        // store's TrySaveAsync revision check directly, the same mechanism VersusSeriesStore
        // relies on - genuine multi-threaded/multi-connection interleaving against Postgres is
        // covered separately by the Infrastructure integration tests, since an in-memory fake
        // can't reproduce a real database race.
        var registered = await _register.ExecuteAsync(new RegisterAccountRequest("frank", "P@ssw0rd!", "Frank"), CancellationToken.None);
        var loadedHash = _refreshTokens.Hash(registered.RefreshToken);
        var sessionA = await _sessions.FindByRefreshTokenHashAsync(loadedHash, CancellationToken.None);
        var sessionB = await _sessions.FindByRefreshTokenHashAsync(loadedHash, CancellationToken.None);
        var expectedRevision = sessionA!.Revision;

        sessionA.Rotate(_refreshTokens.Generate().Hash, _clock.UtcNow, TimeSpan.FromDays(30));
        sessionB!.Rotate(_refreshTokens.Generate().Hash, _clock.UtcNow, TimeSpan.FromDays(30));

        var savedA = await _sessions.TrySaveAsync(sessionA, expectedRevision, CancellationToken.None);
        var savedB = await _sessions.TrySaveAsync(sessionB, expectedRevision, CancellationToken.None);

        Assert.True(savedA);
        Assert.False(savedB);
    }

    [Fact]
    public async Task A_lookup_failure_propagates_instead_of_being_treated_as_an_invalid_refresh_token()
    {
        var useCase = new RefreshSessionUseCase(
            new ThrowingAuthSessionStore(), _accounts, _profiles, _refreshTokens, new FakeAuthSessionPolicy(), new FakeTokenIssuer(), _clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            useCase.ExecuteAsync(new RefreshSessionRequest("whatever"), CancellationToken.None));
    }

    [Fact]
    public async Task A_save_failure_during_rotation_propagates_instead_of_being_treated_as_an_invalid_refresh_token()
    {
        var registered = await _register.ExecuteAsync(new RegisterAccountRequest("grace", "P@ssw0rd!", "Grace"), CancellationToken.None);
        var useCase = new RefreshSessionUseCase(
            new ThrowingOnSaveAuthSessionStore(_sessions), _accounts, _profiles, _refreshTokens, new FakeAuthSessionPolicy(), new FakeTokenIssuer(), _clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            useCase.ExecuteAsync(new RefreshSessionRequest(registered.RefreshToken), CancellationToken.None));
    }
}
