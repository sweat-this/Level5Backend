using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Level5.Domain.Ids;

namespace Level5.Api.Security;

public sealed class CurrentAuthSessionAccessor(IHttpContextAccessor httpContextAccessor)
    : ICurrentAuthSessionAccessor
{
    public AuthSessionId? GetCurrentAuthSessionId()
    {
        var value = httpContextAccessor.HttpContext?.User.FindFirstValue(JwtRegisteredClaimNames.Sid);
        return Guid.TryParse(value, out var id) ? new AuthSessionId(id) : null;
    }
}
