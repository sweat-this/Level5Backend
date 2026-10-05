namespace Level5.Api.Security;

internal static class PasswordSecurityRateLimitPolicyNames
{
    internal const string ResetRequest = "PasswordResetRequestPolicy";
    internal const string ResetComplete = "PasswordResetCompletePolicy";
    internal const string Change = "PasswordChangePolicy";
}
