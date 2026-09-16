using Level5.Application.Common;

namespace Level5.Application.Identity;

/// <summary>
/// One stable failure for every way a refresh credential can be unusable - unknown, expired,
/// revoked, already-rotated (replayed), or owned by a non-active account - so the response never
/// reveals which of those actually happened.
/// </summary>
public sealed class InvalidRefreshTokenException : AppException
{
    public override string Code => "invalid_refresh_token";

    public InvalidRefreshTokenException() : base("Refresh token is invalid, expired, or revoked.")
    {
    }
}
