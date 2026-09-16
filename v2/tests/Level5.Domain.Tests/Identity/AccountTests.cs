using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Xunit;

namespace Level5.Domain.Tests.Identity;

public class AccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Register_defaults_to_active_status()
    {
        var account = Account.Register(Username.Create("patrick"), "hash", Now);

        Assert.Equal(AccountStatus.Active, account.Status);
    }

    [Fact]
    public void Register_without_an_email_leaves_it_null()
    {
        var account = Account.Register(Username.Create("patrick"), "hash", Now);

        Assert.Null(account.Email);
    }

    [Fact]
    public void Register_accepts_an_optional_email()
    {
        var email = Email.Create("patrick@example.com");

        var account = Account.Register(Username.Create("patrick"), "hash", Now, email);

        Assert.Equal(email, account.Email);
    }

    [Fact]
    public void Rehydrate_preserves_a_disabled_status()
    {
        var account = Account.Rehydrate(
            AccountId.New(), Username.Create("patrick"), null, AccountStatus.Disabled, "hash", Now);

        Assert.Equal(AccountStatus.Disabled, account.Status);
    }
}
