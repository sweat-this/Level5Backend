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

    [Fact]
    public void Create_accepts_the_shortest_legal_tag()
    {
        // 2-char handle + '#' + 3-digit discriminator = 6 chars, the grammar's floor.
        var tag = PlayerTag.Create("ab#123");

        Assert.Equal("AB#123", tag.Value);
    }

    [Fact]
    public void Create_accepts_the_longest_legal_tag()
    {
        // 20-char handle + '#' + 6-digit discriminator = 27 chars, the grammar's ceiling. This
        // would have been wrongly rejected by the old MinLength/MaxLength(3/24) pre-check, which
        // disagreed with what the regex (and the registration generator) actually produce.
        var handle = new string('A', 20);
        var tag = PlayerTag.Create($"{handle}#123456");

        Assert.Equal($"{handle}#123456", tag.Value);
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
    public void Create_rejects_a_handle_one_character_past_the_maximum()
    {
        var tooLongHandle = new string('A', 21);

        Assert.Throws<InvalidPlayerTagException>(() => PlayerTag.Create($"{tooLongHandle}#123456"));
    }

    [Fact]
    public void Equality_is_case_insensitive()
    {
        var a = PlayerTag.Create("Patrick#4821");
        var b = PlayerTag.Create("patrick#4821");

        Assert.Equal(a, b);
    }
}
