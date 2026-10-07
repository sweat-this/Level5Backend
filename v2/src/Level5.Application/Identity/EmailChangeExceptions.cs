using Level5.Application.Common;

namespace Level5.Application.Identity;

public sealed class EmailChangeNotAvailableException : AppException
{
    public override string Code => "email_change_not_available";

    public EmailChangeNotAvailableException()
        : base("Email change is not available for this account.")
    {
    }
}

public sealed class EmailAlreadyCurrentException : AppException
{
    public override string Code => "email_already_current";

    public EmailAlreadyCurrentException()
        : base("The requested email is already the account's verified email.")
    {
    }
}

public sealed class EmailChangeCooldownException : AppException
{
    public override string Code => "email_change_cooldown";

    public EmailChangeCooldownException()
        : base("Please wait before requesting another email-change message.")
    {
    }
}

public sealed class InvalidEmailChangeException : AppException
{
    public override string Code => "invalid_email_change";

    public InvalidEmailChangeException()
        : base("The email-change credential is invalid or no longer usable.")
    {
    }
}

public sealed class EmailChangeDeliveryUnavailableException : AppException
{
    public override string Code => "email_change_delivery_unavailable";

    public EmailChangeDeliveryUnavailableException()
        : base("Email-change delivery is temporarily unavailable. Please retry later.")
    {
    }
}
