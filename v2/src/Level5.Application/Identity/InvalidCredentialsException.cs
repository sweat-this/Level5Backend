using Level5.Application.Common;

namespace Level5.Application.Identity;

public sealed class InvalidCredentialsException : AppException
{
    public override string Code => "invalid_credentials";

    public InvalidCredentialsException() : base("Username or password is incorrect.")
    {
    }
}
