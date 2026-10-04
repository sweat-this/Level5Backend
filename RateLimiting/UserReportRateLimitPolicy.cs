using Microsoft.AspNetCore.Http;
using System.Threading.RateLimiting;

namespace Level5Backend.RateLimiting;

public static class UserReportRateLimitPolicy
{
    public const string Name = "UserReportPolicy";

    public static string GetPartitionKey(HttpContext httpContext)
    {
        return httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    public static FixedWindowRateLimiterOptions CreateOptions()
    {
        return new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        };
    }
}
