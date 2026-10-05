using System.Security.Cryptography;
using System.Text;
using Level5.Application.Abstractions;

namespace Level5.Infrastructure.Identity;

public sealed class PasswordResetTokenGenerator : IPasswordResetTokenGenerator
{
    public GeneratedPasswordResetToken Generate()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new(raw, Hash(raw));
    }

    public string Hash(string rawValue) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawValue)));
}
