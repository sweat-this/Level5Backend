using System.IdentityModel.Tokens.Jwt;
using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Infrastructure.Identity;
using Microsoft.Extensions.Options;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

/// <summary>No database needed - this exercises the pure token-issuance behavior of the real ITokenIssuer implementation.</summary>
public sealed class JwtTokenIssuerTests
{
    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private static JwtOptions MakeOptions(int accessTokenLifetimeMinutes) => new()
    {
        Key = "test-only-signing-key-not-for-production-use-32chars-min",
        Issuer = "Level5BackendV2.Tests",
        Audience = "Level5Client.Tests",
        AccessTokenLifetimeMinutes = accessTokenLifetimeMinutes
    };

    [Fact]
    public void IssueAccessToken_derives_expiry_from_the_configured_lifetime_and_injected_clock()
    {
        // Set far from real wall-clock time, so a leftover DateTimeOffset.UtcNow call anywhere in
        // the implementation would make this assertion fail rather than coincidentally pass.
        var fixedNow = new DateTimeOffset(2030, 6, 15, 0, 0, 0, TimeSpan.Zero);
        const int configuredLifetimeMinutes = 37;
        var issuer = new JwtTokenIssuer(
            Options.Create(MakeOptions(configuredLifetimeMinutes)),
            new StubClock(fixedNow));

        var accountId = AccountId.New();
        var sessionId = AuthSessionId.New();
        var token = issuer.IssueAccessToken(accountId, sessionId);
        var encodedToken = new JwtSecurityTokenHandler().ReadJwtToken(token.Value);
        var expectedExpiry = fixedNow.AddMinutes(configuredLifetimeMinutes);

        Assert.Equal(expectedExpiry, token.ExpiresAt);
        Assert.Equal(expectedExpiry.UtcDateTime, encodedToken.ValidTo);
        Assert.Equal(accountId.Value.ToString(), encodedToken.Subject);
        Assert.Equal(sessionId.Value.ToString(), encodedToken.Claims.Single(claim => claim.Type == JwtRegisteredClaimNames.Sid).Value);
        Assert.NotNull(encodedToken.Id);
        Assert.DoesNotContain(encodedToken.Claims, claim => claim.Type is
            "email" or "username" or "player_id" or "session_generation" or "client_kind" or "refresh_token");
    }
}
