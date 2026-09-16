using Level5.Domain.Ids;
using Level5.Domain.Players;
using Xunit;

namespace Level5.Domain.Tests.Players;

public class PlayerProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly PlayerTag Tag = PlayerTag.Create("Patrick#4821");

    [Fact]
    public void Create_trims_the_display_name_before_storing_it()
    {
        var profile = PlayerProfile.Create(AccountId.New(), "  Patrick  ", Tag, Now);

        Assert.Equal("Patrick", profile.DisplayName);
    }

    [Fact]
    public void Create_validates_length_after_trimming_not_before()
    {
        // 32 letters padded with whitespace to 40 raw chars - only valid once trimmed first.
        var padded = "  " + new string('A', 32) + "  ";

        var profile = PlayerProfile.Create(AccountId.New(), padded, Tag, Now);

        Assert.Equal(32, profile.DisplayName.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_empty_or_whitespace_only_names(string raw)
    {
        Assert.Throws<InvalidDisplayNameException>(() => PlayerProfile.Create(AccountId.New(), raw, Tag, Now));
    }

    [Fact]
    public void Create_rejects_a_name_that_still_exceeds_the_limit_after_trimming()
    {
        var tooLong = new string('A', 33);

        Assert.Throws<InvalidDisplayNameException>(() => PlayerProfile.Create(AccountId.New(), tooLong, Tag, Now));
    }
}
