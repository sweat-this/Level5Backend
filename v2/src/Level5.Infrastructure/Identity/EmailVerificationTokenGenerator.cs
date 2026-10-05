using System.Security.Cryptography;
using System.Text;
using Level5.Application.Abstractions;

namespace Level5.Infrastructure.Identity;

public sealed class EmailVerificationTokenGenerator : IEmailVerificationTokenGenerator
{
    private const int RawTokenByteLength = 32;

    public GeneratedEmailVerificationToken Generate()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(RawTokenByteLength))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return new GeneratedEmailVerificationToken(raw, Hash(raw));
    }

    public string Hash(string rawValue)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawValue)));
}
