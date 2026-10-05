using Level5.Domain.Platform;
using Xunit;

namespace Level5.Domain.Tests.Platform;

public sealed class ProductIdTests
{
    [Theory]
    [InlineData("level5", "level5")]
    [InlineData(" LEVEL5 ", "level5")]
    [InlineData("secret-robot", "secret-robot")]
    [InlineData("game2", "game2")]
    public void Create_canonicalizes_valid_machine_identifiers(string raw, string expected)
    {
        Assert.Equal(expected, ProductId.Create(raw).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("level 5")]
    [InlineData("level_5")]
    [InlineData("-level5")]
    [InlineData("level5-")]
    [InlineData("level5--base")]
    public void Create_rejects_invalid_identifiers(string raw)
    {
        Assert.Throws<InvalidProductIdException>(() => ProductId.Create(raw));
    }

    [Fact]
    public void Create_rejects_identifiers_beyond_the_persistence_bound()
    {
        Assert.Throws<InvalidProductIdException>(() => ProductId.Create(new string('a', ProductId.MaxLength + 1)));
    }
}
