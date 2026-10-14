using Level5.Domain.Platform;

namespace Level5.Domain.Tests.Platform;

public sealed class BugReportTests
{
    [Theory]
    [InlineData("level5")]
    [InlineData("zromp")]
    [InlineData("secret-robot")]
    [InlineData("blood-money")]
    public void Supported_games_and_normalization(string game)
    {
        var content = BugReportContent.Create(game, " title ", " a\r\nb\rc ", " other ", "  ", " 1.0 ");
        Assert.Equal(BugReportContent.Create(game, "title", "a\nb\nc", "other", null, "1.0"), content);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Level5")]
    [InlineData("unknown")]
    [InlineData(" level5 ")]
    public void Unsupported_games_are_rejected(string? game)
        => Assert.Throws<InvalidBugReportException>(() => BugReportContent.Create(game, "title", "description"));

    [Theory]
    [InlineData("title", 160)]
    [InlineData("description", 8000)]
    [InlineData("steps", 8000)]
    [InlineData("version", 64)]
    public void Text_limits_apply_to_raw_input(string field, int maximum)
    {
        BugReportContent Create(string value) => BugReportContent.Create("level5", field == "title" ? value : "title",
            field == "description" ? value : "description", null, field == "steps" ? value : null, field == "version" ? value : null);
        Assert.NotNull(Create(new string('x', maximum)));
        Assert.Throws<InvalidBugReportException>(() => Create(new string('x', maximum + 1)));
        Assert.Throws<InvalidBugReportException>(() => Create("\0"));
    }

    [Theory]
    [InlineData(null, "description")]
    [InlineData(" ", "description")]
    [InlineData("title", null)]
    [InlineData("title", "\r\n\t")]
    public void Required_text_is_validated(string? title, string? description)
        => Assert.Throws<InvalidBugReportException>(() => BugReportContent.Create("zromp", title, description));

    [Theory]
    [InlineData(null)]
    [InlineData("gameplay")]
    [InlineData("technical")]
    [InlineData("interface")]
    [InlineData("other")]
    public void Categories_are_allowlisted(string? category)
    {
        Assert.NotNull(BugReportContent.Create("level5", "title", "<script>plain text</script>", category));
        Assert.Throws<InvalidBugReportException>(() => BugReportContent.Create("level5", "title", "description", "invalid"));
    }
}
