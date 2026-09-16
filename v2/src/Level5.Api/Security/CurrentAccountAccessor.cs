using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Level5.Domain.Ids;

namespace Level5.Api.Security;

/// <summary>
/// The single place that knows a JWT's `sub` claim encodes the authenticated <see cref="AccountId"/> -
/// pure claim parsing, no persistence lookups, so every other authenticated concern
/// (<see cref="CurrentPlayerProvider"/>, <c>/api/v2/me</c>) shares exactly one implementation
/// instead of re-deriving it from <see cref="ClaimsPrincipal"/> independently.
/// </summary>
public sealed class CurrentAccountAccessor(IHttpContextAccessor httpContextAccessor) : ICurrentAccountAccessor
{
    public AccountId GetCurrentAccountId()
    {
        var user = httpContextAccessor.HttpContext?.User
            ?? throw new InvalidOperationException("No HTTP context available.");

        var subject = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? throw new InvalidOperationException("Authenticated request is missing a subject claim.");

        return new AccountId(Guid.Parse(subject));
    }
}
