using Level5.Domain.Players;
using Xunit;

namespace Level5.Domain.Tests.Players;

public class PlayerTagTests
{
    [Theory]
    [InlineData("Patrick#4821")]
    [InlineData("ab#000")]
    [InlineData("Player_Name#123456")]
    public void Create_accepts_well_formed_tags(string raw)
    {
        var tag = PlayerTag.Create(raw);

        Assert.Equal(raw.ToUpperInvariant(), tag.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nodiscriminator")]
    [InlineData("Patrick#12")]
    [InlineData("Patrick#1234567")]
    [InlineData("has space#1234")]
    [InlineData("a#123")]
    public void Create_rejects_malformed_tags(string raw)
    {
        Assert.Throws<InvalidPlayerTagException>(() => PlayerTag.Create(raw));
    }

    [Fact]
    public void Equality_is_case_insensitive()
    {
        var a = PlayerTag.Create("Patrick#4821");
        var b = PlayerTag.Create("patrick#4821");

        Assert.Equal(a, b);
    }
}
