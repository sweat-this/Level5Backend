using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Level5.Application.Abstractions;
using Level5.Domain.Ids;

namespace Level5.Api.Security;

public sealed class CurrentPlayerProvider(IHttpContextAccessor httpContextAccessor, IPlayerProfileStore playerProfileStore) : ICurrentPlayerProvider
{
    public async Task<PlayerId> GetCurrentPlayerIdAsync(CancellationToken cancellationToken)
    {
        var user = httpContextAccessor.HttpContext?.User
            ?? throw new InvalidOperationException("No HTTP context available.");

        var subject = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? throw new InvalidOperationException("Authenticated request is missing a subject claim.");

        var accountId = new AccountId(Guid.Parse(subject));

        var profile = await playerProfileStore.FindByAccountIdAsync(accountId, cancellationToken)
            ?? throw new InvalidOperationException($"Account {accountId} has no player profile.");

        return profile.Id;
    }
}
