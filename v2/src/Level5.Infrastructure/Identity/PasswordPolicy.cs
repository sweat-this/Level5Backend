using Level5.Application.Abstractions;
using Level5.Application.Identity;

namespace Level5.Infrastructure.Identity;

/// <summary>
/// The one place password-strength rules are enforced (see <see cref="IPasswordPolicy"/>).
///
/// Deliberately conservative and NIST 800-63B-aligned rather than a full ASP.NET Core Identity
/// <c>UserManager</c>/<c>IPasswordValidator&lt;TUser&gt;</c> pipeline: that machinery expects the
/// full Identity user-store shape, which this codebase's V2 ADR already rejected (see
/// <c>AspNetPasswordHasher</c> and the V2 README) in favor of using Identity's individual
/// primitives - here, just <c>PasswordOptions</c>'s well-established minimum-length convention -
/// behind an application-facing port.
///
/// No forced character-class complexity (uppercase/digit/symbol) or arbitrary special-character
/// rules: current NIST guidance considers those to push users toward predictable patterns without
/// materially improving resistance to guessing, and no V2 product requirement calls for them. A
/// minimum length and a generous maximum (bounding the cost of hashing pathological input, not a
/// meaningful security control) are the only rules enforced. Revisit this if a concrete product
/// requirement (e.g. a breached-password check) emerges - do not add complexity rules
/// speculatively.
/// </summary>
public sealed class PasswordPolicy : IPasswordPolicy
{
    public const int MinimumLength = 8;
    public const int MaximumLength = 128;

    public void Validate(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumLength)
        {
            throw new PasswordPolicyViolationException($"Password must be at least {MinimumLength} characters.");
        }

        if (password.Length > MaximumLength)
        {
            throw new PasswordPolicyViolationException($"Password cannot exceed {MaximumLength} characters.");
        }
    }
}
