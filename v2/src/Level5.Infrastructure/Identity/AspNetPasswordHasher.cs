using Level5.Application.Abstractions;
using Level5.Domain.Identity;
using Microsoft.AspNetCore.Identity;

namespace Level5.Infrastructure.Identity;

/// <summary>
/// Wraps ASP.NET Core Identity's battle-tested PBKDF2 hasher rather than rolling a custom
/// scheme. <see cref="Account"/> is used only as PasswordHasher's generic type parameter - its
/// members are never touched by the hasher itself.
/// </summary>
public sealed class AspNetPasswordHasher : IPasswordHasher
{
    private readonly PasswordHasher<Account> _inner = new();

    public string Hash(string password) => _inner.HashPassword(null!, password);

    public Level5.Application.Abstractions.PasswordVerificationResult Verify(string passwordHash, string suppliedPassword)
        => _inner.VerifyHashedPassword(null!, passwordHash, suppliedPassword) switch
        {
            Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success => Level5.Application.Abstractions.PasswordVerificationResult.Success,
            Microsoft.AspNetCore.Identity.PasswordVerificationResult.SuccessRehashNeeded => Level5.Application.Abstractions.PasswordVerificationResult.SuccessRehashNeeded,
            _ => Level5.Application.Abstractions.PasswordVerificationResult.Failed
        };
}
