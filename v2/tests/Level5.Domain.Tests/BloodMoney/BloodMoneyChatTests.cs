using System.Text;
using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;

namespace Level5.Domain.Tests.BloodMoney;

public sealed class BloodMoneyChatTests
{
    [Theory]
    [InlineData(" \u2003e\u0301\r\nx\ry\n ", "é\nx\ny")]
    [InlineData("a\nb", "a\nb")]
    public void Normalize_applies_newlines_NFC_and_outer_Unicode_whitespace(string raw, string expected)
        => Assert.Equal(expected, BloodMoneyChatText.Normalize(raw));

    [Theory]
    [InlineData("\ttext")][InlineData("text\0")][InlineData("a\u007fb")][InlineData("a\u0085b")]
    [InlineData("")][InlineData(" \u2003\n ")][InlineData(null)]
    public void Reject_empty_and_controls_before_trimming(string? raw)
        => Assert.Throws<InvalidBloodMoneyChatMessageException>(() => BloodMoneyChatText.Normalize(raw));

    [Fact]
    public void Reject_unpaired_surrogates_without_replacement()
    {
        foreach (var raw in new[] { "\ud800", "x\udfff", "\ud800x", "\udfff\ud800" })
            Assert.Throws<InvalidBloodMoneyChatMessageException>(() => BloodMoneyChatText.Normalize(raw));
    }

    [Fact]
    public void Scalar_and_UTF8_bounds_count_supplementary_characters_once()
    {
        var boundary = string.Concat(Enumerable.Repeat("😀", 500));
        Assert.Equal(1000, boundary.Length);
        Assert.Equal(2000, Encoding.UTF8.GetByteCount(boundary));
        Assert.Equal(boundary, BloodMoneyChatText.Normalize(boundary));
        Assert.Throws<InvalidBloodMoneyChatMessageException>(() => BloodMoneyChatText.Normalize(boundary + "a")); // 2001 bytes
        Assert.Throws<InvalidBloodMoneyChatMessageException>(() => BloodMoneyChatText.Normalize(new string('a', 501)));
        Assert.Equal(new string('a', 500), BloodMoneyChatText.Normalize(new string('a', 500)));
        // Valid Unicode has at most four UTF-8 bytes per scalar: >2000 bytes necessarily also exceeds 500 scalars.
    }

    [Fact]
    public void Accepted_message_is_immutable_and_rejects_invalid_identity_sequence_or_unnormalized_body()
    {
        var id = Guid.NewGuid(); var challenge = new BloodMoneyChallengeId(Guid.NewGuid()); var sender = PlayerId.New();
        var key = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var message = new BloodMoneyChatMessage(id, challenge, 1, sender, key, "hello", now, BloodMoneyChatVisibility.Visible);
        Assert.Equal("hello", message.Body);
        Assert.All(typeof(BloodMoneyChatMessage).GetProperties(), property => Assert.Null(property.SetMethod));
        foreach (var create in new Action[]
        {
            () => new BloodMoneyChatMessage(Guid.Empty, challenge, 1, sender, key, "hello", now, BloodMoneyChatVisibility.Visible),
            () => new BloodMoneyChatMessage(id, challenge, 0, sender, key, "hello", now, BloodMoneyChatVisibility.Visible),
            () => new BloodMoneyChatMessage(id, challenge, 1, sender, Guid.Empty, "hello", now, BloodMoneyChatVisibility.Visible),
            () => new BloodMoneyChatMessage(id, challenge, 1, sender, key, " hello ", now, BloodMoneyChatVisibility.Visible),
            () => new BloodMoneyChatMessage(id, challenge, 1, sender, key, "hello", now, (BloodMoneyChatVisibility)99)
        }) Assert.Throws<InvalidBloodMoneyChatMessageException>(create);
    }
}
