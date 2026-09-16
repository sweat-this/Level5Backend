using Level5.Application.Identity;
using Level5.Infrastructure.Identity;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

/// <summary>No database needed - this exercises the centralized password-policy implementation directly.</summary>
public sealed class PasswordPolicyTests
{
    private readonly PasswordPolicy _policy = new();

    [Theory]
    [InlineData("P@ssw0rd!")]
    [InlineData("correct horse battery staple")]
    [InlineData("exactly8")]
    public void Accepts_passwords_meeting_the_minimum_length(string password)
    {
        _policy.Validate(password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short1!")]
    public void Rejects_passwords_shorter_than_the_minimum_length(string password)
    {
        Assert.Throws<PasswordPolicyViolationException>(() => _policy.Validate(password));
    }

    [Fact]
    public void Rejects_passwords_longer_than_the_maximum_length()
    {
        var tooLong = new string('a', PasswordPolicy.MaximumLength + 1);

        Assert.Throws<PasswordPolicyViolationException>(() => _policy.Validate(tooLong));
    }

    [Fact]
    public void Does_not_require_any_particular_character_class()
    {
        // Deliberate: no digit/uppercase/symbol requirement - see PasswordPolicy's own doc comment.
        _policy.Validate("alllowercaseandlongenough");
    }
}
