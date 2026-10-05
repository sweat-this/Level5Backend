using Level5.Application.Common;

namespace Level5.Application.Identity;

public sealed class InvalidPasswordResetException : AppException
{
    public override string Code => "invalid_password_reset";
    public InvalidPasswordResetException() : base("The password-reset credential is invalid or no longer usable.") { }
}
