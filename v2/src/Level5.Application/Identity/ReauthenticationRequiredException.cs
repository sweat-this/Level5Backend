using Level5.Application.Common;

namespace Level5.Application.Identity;

/// <summary>
/// One stable response for missing/malformed session context and every inactive caller session,
/// without revealing whether a session is unknown, revoked, expired, stale, or account-disabled.
/// </summary>
public sealed class ReauthenticationRequiredException : AppException
{
    public override string Code => "reauthentication_required";

    public ReauthenticationRequiredException()
        : base("A current authenticated session is required. Please sign in again.")
    {
    }
}
