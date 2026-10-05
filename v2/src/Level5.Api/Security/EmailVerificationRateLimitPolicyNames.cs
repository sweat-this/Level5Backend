namespace Level5.Api.Security;

internal static class EmailVerificationRateLimitPolicyNames
{
    internal const string Request = "EmailVerificationRequestPolicy";
    internal const string Resend = "EmailVerificationResendPolicy";
    internal const string Complete = "EmailVerificationCompletePolicy";
}
