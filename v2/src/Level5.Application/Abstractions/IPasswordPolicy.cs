namespace Level5.Application.Abstractions;

/// <summary>
/// The single centralized place password-strength rules are enforced, so no controller or use
/// case needs its own ad hoc checks. Implementations throw
/// <c>Level5.Application.Identity.PasswordPolicyViolationException</c> on failure.
/// </summary>
public interface IPasswordPolicy
{
    void Validate(string password);
}
