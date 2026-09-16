using Level5.Application.Common;
using Level5.Application.Identity;
using Level5.Application.Tests.Fakes;
using Xunit;

namespace Level5.Application.Tests.Identity;

public class RegisterAccountUseCaseTests
{
    private readonly InMemoryAccountStore _accounts = new();
    private readonly InMemoryPlayerProfileStore _profiles = new();
    private readonly InMemoryAuthSessionStore _sessions = new();
    private readonly RegisterAccountUseCase _useCase;

    public RegisterAccountUseCaseTests()
    {
        _useCase = new RegisterAccountUseCase(
            _accounts, _profiles, _sessions,
            new FakePasswordHasher(), new FakePasswordPolicy(),
            new FakeRefreshTokenGenerator(), new FakeAuthSessionPolicy(),
            new FakeTokenIssuer(), new NoOpUnitOfWork(), new FakeClock());
    }

    [Fact]
    public async Task Registering_creates_an_account_and_a_matching_player_profile()
    {
        var result = await _useCase.ExecuteAsync(new RegisterAccountRequest("patrick", "P@ssw0rd!", "Patrick"), CancellationToken.None);

        Assert.StartsWith("PATRICK#", result.PlayerTag);
        var profile = await _profiles.FindByIdAsync(result.PlayerId, CancellationToken.None);
        Assert.NotNull(profile);
        Assert.Equal(result.AccountId, profile!.AccountId);
    }

    [Fact]
    public async Task Registering_a_taken_username_is_rejected()
    {
        await _useCase.ExecuteAsync(new RegisterAccountRequest("patrick", "P@ssw0rd!", "Patrick"), CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _useCase.ExecuteAsync(new RegisterAccountRequest("Patrick", "OtherPassword!", "Someone"), CancellationToken.None));
    }

    [Fact]
    public async Task Issues_an_access_token_on_success()
    {
        var result = await _useCase.ExecuteAsync(new RegisterAccountRequest("bob", "P@ssw0rd!", "Bob"), CancellationToken.None);

        Assert.Contains(result.AccountId.Value.ToString(), result.AccessToken.Value);
    }

    [Fact]
    public async Task Registering_with_a_long_display_name_still_produces_a_valid_tag()
    {
        // The generated handle is truncated to 20 chars, then combined with a '#' and a 4-digit
        // discriminator - 25 chars total, valid per the PlayerTag grammar (max 27) but previously
        // rejected by PlayerTag's own drifted MaxLength(24) pre-check.
        var longDisplayName = new string('A', 30);

        var result = await _useCase.ExecuteAsync(
            new RegisterAccountRequest("longname", "P@ssw0rd!", longDisplayName), CancellationToken.None);

        Assert.StartsWith(new string('A', 20) + "#", result.PlayerTag);
    }

    [Fact]
    public async Task Registering_also_creates_a_refresh_session_and_returns_its_credential()
    {
        var result = await _useCase.ExecuteAsync(new RegisterAccountRequest("carol", "P@ssw0rd!", "Carol"), CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(result.RefreshToken));
        Assert.True(result.RefreshTokenExpiresAt > new FakeClock().UtcNow);

        var session = await _sessions.FindByRefreshTokenHashAsync(new FakeRefreshTokenGenerator().Hash(result.RefreshToken), CancellationToken.None);
        Assert.NotNull(session);
        Assert.Equal(result.AccountId, session!.AccountId);
    }

    [Fact]
    public async Task Registering_with_a_password_that_fails_policy_does_not_create_an_account()
    {
        await Assert.ThrowsAsync<PasswordPolicyViolationException>(() =>
            _useCase.ExecuteAsync(new RegisterAccountRequest("dave", "", "Dave"), CancellationToken.None));

        Assert.False(await _accounts.UsernameExistsAsync(Level5.Domain.Identity.Username.Create("dave"), CancellationToken.None));
    }
}
