using Level5.Infrastructure.Identity;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

/// <summary>No database needed - this exercises the pure cryptographic behavior of the refresh-token port's real implementation.</summary>
public sealed class RefreshTokenGeneratorTests
{
    [Fact]
    public void Generate_produces_a_high_entropy_raw_value_and_its_hash()
    {
        var generator = new RefreshTokenGenerator();

        var token = generator.Generate();

        Assert.False(string.IsNullOrWhiteSpace(token.RawValue));
        Assert.True(token.RawValue.Length >= 32);
        Assert.Equal(generator.Hash(token.RawValue), token.Hash);
    }

    [Fact]
    public void Generate_never_produces_the_same_raw_value_twice()
    {
        var generator = new RefreshTokenGenerator();

        var first = generator.Generate();
        var second = generator.Generate();

        Assert.NotEqual(first.RawValue, second.RawValue);
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void Hash_is_deterministic_for_the_same_raw_value()
    {
        var generator = new RefreshTokenGenerator();
        var token = generator.Generate();

        var rehashed = generator.Hash(token.RawValue);

        Assert.Equal(token.Hash, rehashed);
    }

    [Fact]
    public void Hash_does_not_reproduce_the_raw_value()
    {
        var generator = new RefreshTokenGenerator();
        var token = generator.Generate();

        Assert.NotEqual(token.RawValue, token.Hash);
    }
}
