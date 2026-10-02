namespace Level5.Api.Security;

internal static class AuthRateLimitPolicyNames
{
    internal const string Register = "AuthRegisterPolicy";
    internal const string Login = "AuthLoginPolicy";
    internal const string Refresh = "AuthRefreshPolicy";
    internal const string Logout = "AuthLogoutPolicy";
}
