using Level5.Domain.Identity;
using Xunit;

namespace Level5.Domain.Tests.Identity;

public class EmailTests
{
    [Theory]
    [InlineData("patrick@example.com")]
    [InlineData("first.last@sub.example.co")]
    public void Create_accepts_well_formed_addresses(string raw)
    {
        var email = Email.Create(raw);

        Assert.Equal(raw, email.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("no-domain@")]
    [InlineData("@no-local.com")]
    [InlineData("no-tld@example")]
    [InlineData("has space@example.com")]
    public void Create_rejects_malformed_addresses(string raw)
    {
        Assert.Throws<InvalidEmailException>(() => Email.Create(raw));
    }

    [Fact]
    public void Equality_is_case_insensitive()
    {
        var a = Email.Create("Patrick@Example.com");
        var b = Email.Create("patrick@example.com");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Value_preserves_original_casing_while_canonical_is_lowercase()
    {
        var email = Email.Create("Patrick@Example.com");

        Assert.Equal("Patrick@Example.com", email.Value);
        Assert.Equal("patrick@example.com", email.Canonical);
    }

    [Fact]
    public void Create_accepts_an_address_at_the_maximum_length()
    {
        // 300-char local part + '@' + 15-char domain + ".com" = 320 chars, the column's ceiling.
        var address = new string('a', 300) + "@" + new string('b', 15) + ".com";

        var email = Email.Create(address);

        Assert.Equal(320, email.Value.Length);
    }

    [Fact]
    public void Create_rejects_an_address_one_character_past_the_maximum()
    {
        var tooLong = new string('a', 301) + "@" + new string('b', 15) + ".com";

        Assert.Throws<InvalidEmailException>(() => Email.Create(tooLong));
    }
}
