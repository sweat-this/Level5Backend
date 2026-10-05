using Level5.Application.Common;

namespace Level5.Application.Identity;

public sealed class CurrentPasswordInvalidException : AppException
{
    public override string Code => "invalid_current_password";

    public CurrentPasswordInvalidException() : base("The current password is incorrect.")
    {
    }
}

public sealed class AccountSecurityActionForbiddenException : AppException
{
    public override string Code => "account_security_action_forbidden";

    public AccountSecurityActionForbiddenException() : base("The account cannot perform this security operation.")
    {
    }
}

public sealed class EmailUnavailableException : AppException
{
    public override string Code => "email_unavailable";

    public EmailUnavailableException() : base("The requested email is unavailable.")
    {
    }
}

public sealed class EmailVerificationNotAvailableException : AppException
{
    public override string Code => "email_verification_not_available";

    public EmailVerificationNotAvailableException()
        : base("Email verification is not available for this account.")
    {
    }
}

public sealed class EmailVerificationCooldownException : AppException
{
    public override string Code => "email_verification_cooldown";

    public EmailVerificationCooldownException()
        : base("Please wait before requesting another verification email.")
    {
    }
}

public sealed class InvalidEmailVerificationException : AppException
{
    public override string Code => "invalid_email_verification";

    public InvalidEmailVerificationException()
        : base("The email-verification credential is invalid or no longer usable.")
    {
    }
}

public sealed class EmailVerificationDeliveryUnavailableException : AppException
{
    public override string Code => "email_verification_delivery_unavailable";

    public EmailVerificationDeliveryUnavailableException()
        : base("Email verification delivery is temporarily unavailable. Please retry later.")
    {
    }
}
