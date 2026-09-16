using Level5.Application.Common;

namespace Level5.Application.Identity;

public sealed class PasswordPolicyViolationException : AppException
{
    public override string Code => "weak_password";

    public PasswordPolicyViolationException(string message) : base(message)
    {
    }
}
