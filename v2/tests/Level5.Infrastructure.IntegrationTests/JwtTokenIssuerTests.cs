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

    private static JwtOptions MakeOptions() => new()
    {
        Key = "test-only-signing-key-not-for-production-use-32chars-min",
        Issuer = "Level5BackendV2.Tests",
        Audience = "Level5Client.Tests",
        AccessTokenLifetimeMinutes = 15
    };

    [Fact]
    public void IssueAccessToken_derives_expiry_from_the_injected_clock_not_wall_clock_time()
    {
        // Set far from real wall-clock time, so a leftover DateTimeOffset.UtcNow call anywhere in
        // the implementation would make this assertion fail rather than coincidentally pass.
        var fixedNow = new DateTimeOffset(2030, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var issuer = new JwtTokenIssuer(Options.Create(MakeOptions()), new StubClock(fixedNow));

        var token = issuer.IssueAccessToken(AccountId.New());

        Assert.Equal(fixedNow.AddMinutes(15), token.ExpiresAt);
    }
}
