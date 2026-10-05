using Level5.Domain.Ids;

namespace Level5.Application.Abstractions;

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues short-lived access tokens bound to the durable refresh session that created them.
/// </summary>
public interface ITokenIssuer
{
    AccessToken IssueAccessToken(AccountId accountId, AuthSessionId authSessionId);
}
