using Level5.Domain.Identity;

namespace Level5.Api.Security;

public sealed class ClientKindAccessor(IHttpContextAccessor httpContextAccessor) : IClientKindAccessor
{
    public const string HeaderName = "X-SweatThis-Client";

    public ClientKind GetClientKind()
    {
        var value = httpContextAccessor.HttpContext?.Request.Headers[HeaderName].ToString();
        return value?.Trim().ToLowerInvariant() switch
        {
            "web" => ClientKind.Web,
            "unity" => ClientKind.Unity,
            _ => ClientKind.Unknown
        };
    }
}
