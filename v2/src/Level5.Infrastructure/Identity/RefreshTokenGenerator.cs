using System.Security.Cryptography;
using System.Text;
using Level5.Application.Abstractions;

namespace Level5.Infrastructure.Identity;

/// <summary>
/// Refresh credentials are high-entropy opaque secrets, not low-entropy human input, so this
/// deliberately does not reuse <see cref="AspNetPasswordHasher"/> (slow, salted, designed to
/// resist guessing a weak password) - a fast unsalted SHA-256 digest is the right tool for
/// exact-match lookup of an already-random 256-bit secret. The raw value is generated from
/// <see cref="RandomNumberGenerator"/> (a CSPRNG), is never derived from any account/session id,
/// and this type never logs it.
/// </summary>
public sealed class RefreshTokenGenerator : IRefreshTokenGenerator
{
    private const int RawTokenByteLength = 32; // 256 bits of entropy

    public GeneratedRefreshToken Generate()
    {
        var raw = ToBase64Url(RandomNumberGenerator.GetBytes(RawTokenByteLength));
        return new GeneratedRefreshToken(raw, Hash(raw));
    }

    public string Hash(string rawValue)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawValue)));

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
