using Level5.Infrastructure.Identity;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

public sealed class PasswordResetTokenGeneratorTests
{
    [Fact]
    public void Generate_uses_a_256_bit_base64url_secret_and_sha256_hash()
    {
        var generator = new PasswordResetTokenGenerator();
        var token = generator.Generate();

        Assert.Equal(43, token.RawValue.Length);
        Assert.DoesNotContain('=', token.RawValue);
        Assert.DoesNotContain('+', token.RawValue);
        Assert.DoesNotContain('/', token.RawValue);
        Assert.Equal(64, token.Hash.Length);
        Assert.Equal(generator.Hash(token.RawValue), token.Hash);
    }

    [Fact]
    public void Generate_rotates_to_distinct_credentials()
    {
        var generator = new PasswordResetTokenGenerator();
        var first = generator.Generate();
        var second = generator.Generate();
        Assert.NotEqual(first.RawValue, second.RawValue);
        Assert.NotEqual(first.Hash, second.Hash);
    }
}
