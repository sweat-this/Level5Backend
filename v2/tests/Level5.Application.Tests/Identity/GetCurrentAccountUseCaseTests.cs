using Level5.Application.Identity;
using Level5.Application.Tests.Fakes;
using Xunit;

namespace Level5.Application.Tests.Identity;

public class GetCurrentAccountUseCaseTests
{
    private readonly InMemoryAccountStore _accounts = new();
    private readonly InMemoryPlayerProfileStore _profiles = new();
    private readonly InMemoryAuthSessionStore _sessions = new();
    private readonly RegisterAccountUseCase _register;
    private readonly GetCurrentAccountUseCase _getCurrentAccount;

    public GetCurrentAccountUseCaseTests()
    {
        _register = new RegisterAccountUseCase(
            _accounts, _profiles, _sessions, new FakePasswordHasher(), new FakePasswordPolicy(),
            new FakeRefreshTokenGenerator(), new FakeAuthSessionPolicy(), new FakeTokenIssuer(), new NoOpUnitOfWork(), new FakeClock());
        _getCurrentAccount = new GetCurrentAccountUseCase(_accounts, _profiles);
    }

    [Fact]
    public async Task Returns_the_authenticated_accounts_own_view()
    {
        var registered = await _register.ExecuteAsync(new RegisterAccountRequest("patrick", "P@ssw0rd!", "Patrick"), CancellationToken.None);

        var view = await _getCurrentAccount.ExecuteAsync(registered.AccountId, CancellationToken.None);

        Assert.Equal(registered.AccountId, view.AccountId);
        Assert.Equal(registered.PlayerId, view.PlayerId);
        Assert.Equal("patrick", view.Username);
        Assert.Equal(Level5.Domain.Identity.AccountStatus.Active, view.Status);
    }
}
