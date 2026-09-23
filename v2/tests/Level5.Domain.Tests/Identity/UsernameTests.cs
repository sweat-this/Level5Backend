using Level5.Domain.Identity;
using Xunit;

namespace Level5.Domain.Tests.Identity;

public class UsernameTests
{
    [Theory]
    [InlineData("patrick")]
    [InlineData("Pat.rick_99")]
    public void Create_accepts_well_formed_usernames(string raw)
    {
        var username = Username.Create(raw);

        Assert.Equal(raw, username.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("has space")]
    [InlineData("way-too-long-a-username-for-this-system")]
    public void Create_rejects_malformed_usernames(string raw)
    {
        Assert.Throws<InvalidUsernameException>(() => Username.Create(raw));
    }

    [Fact]
    public void Equality_is_case_insensitive()
    {
        var a = Username.Create("Patrick");
        var b = Username.Create("patrick");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Value_preserves_original_casing()
    {
        var username = Username.Create("Patrick");

        Assert.Equal("Patrick", username.Value);
        Assert.Equal("PATRICK", username.Canonical);
    }
}
