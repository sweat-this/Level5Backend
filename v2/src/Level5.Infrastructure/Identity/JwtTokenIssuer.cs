using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Level5.Infrastructure.Identity;

/// <summary>
/// Issues short-lived JWT access tokens. The token carries only what authorization needs - the
/// account id as <c>sub</c> and originating auth-session id as <c>sid</c> - and never email,
/// name, session generation, or other profile/session data.
/// </summary>
public sealed class JwtTokenIssuer(IOptions<JwtOptions> options, IClock clock) : ITokenIssuer
{
    public AccessToken IssueAccessToken(AccountId accountId, AuthSessionId authSessionId)
    {
        var settings = options.Value;
        var expiresAt = clock.UtcNow.AddMinutes(settings.AccessTokenLifetimeMinutes);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, accountId.Value.ToString()),
            new Claim(JwtRegisteredClaimNames.Sid, authSessionId.Value.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString())
        };

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Key));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: settings.Issuer,
            audience: settings.Audience,
            claims: claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
