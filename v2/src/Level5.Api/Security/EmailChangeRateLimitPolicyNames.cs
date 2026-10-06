namespace Level5.Api.Security;

internal static class EmailChangeRateLimitPolicyNames
{
    internal const string Request = "EmailChangeRequestPolicy";
    internal const string Resend = "EmailChangeResendPolicy";
    internal const string Complete = "EmailChangeCompletePolicy";
}
