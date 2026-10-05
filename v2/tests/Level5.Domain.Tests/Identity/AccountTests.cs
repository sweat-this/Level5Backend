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
        Assert.Equal(0, account.SessionGeneration);
    }

    [Fact]
    public void Credential_change_increments_generation_but_hash_maintenance_does_not()
    {
        var account = Account.Register(Username.Create("patrick"), "hash", Now);

        account.MaintainPasswordHash("rehash");
        Assert.Equal(0, account.SessionGeneration);

        account.ChangePassword("new-hash");
        Assert.Equal(1, account.SessionGeneration);
        Assert.Equal("new-hash", account.PasswordHash);
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
        Assert.Null(account.EmailVerifiedAt);
    }

    [Fact]
    public void Rehydrate_preserves_a_disabled_status()
    {
        var account = Account.Rehydrate(
            AccountId.New(), Username.Create("patrick"), null, AccountStatus.Disabled, "hash", Now);

        Assert.Equal(AccountStatus.Disabled, account.Status);
    }

    [Fact]
    public void Rehydrate_rejects_verification_state_without_an_email()
    {
        Assert.Throws<ArgumentException>(() => Account.Rehydrate(
            AccountId.New(), Username.Create("patrick"), null, AccountStatus.Active, "hash", Now, Now));
    }

    [Fact]
    public void VerifyEmail_records_timestamp_only_for_current_canonical_address()
    {
        var account = Account.Register(
            Username.Create("patrick"), "hash", Now, Email.Create("Patrick@Example.com"));

        account.VerifyEmail(Email.Create("patrick@example.com"), Now.AddMinutes(1));

        Assert.Equal(Now.AddMinutes(1), account.EmailVerifiedAt);
    }

    [Fact]
    public void VerifyEmail_rejects_a_different_address()
    {
        var account = Account.Register(
            Username.Create("patrick"), "hash", Now, Email.Create("one@example.com"));

        Assert.Throws<EmailVerificationTargetMismatchException>(() =>
            account.VerifyEmail(Email.Create("two@example.com"), Now));
    }

    [Fact]
    public void Replacing_an_unverified_address_clears_verification_state()
    {
        var account = Account.Register(
            Username.Create("patrick"), "hash", Now, Email.Create("one@example.com"));

        Assert.True(account.AttachOrReplaceUnverifiedEmail(Email.Create("two@example.com")));

        Assert.Equal("two@example.com", account.Email!.Value);
        Assert.Null(account.EmailVerifiedAt);
    }

    [Fact]
    public void A_verified_address_cannot_be_changed_but_same_canonical_address_is_a_no_op()
    {
        var email = Email.Create("Patrick@Example.com");
        var account = Account.Rehydrate(
            AccountId.New(), Username.Create("patrick"), email, AccountStatus.Active, "hash", Now, Now);

        Assert.False(account.AttachOrReplaceUnverifiedEmail(Email.Create("patrick@example.com")));
        Assert.Throws<VerifiedEmailChangeNotAllowedException>(() =>
            account.AttachOrReplaceUnverifiedEmail(Email.Create("other@example.com")));
    }
}
