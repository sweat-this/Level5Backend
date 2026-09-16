using Level5.Domain.Ids;

namespace Level5.Application.Abstractions;

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues short-lived access tokens. Deliberately minimal: no refresh/session rotation yet -
/// that is a follow-up slice once the foundation's session model is decided, not something to
/// half-build here.
/// </summary>
public interface ITokenIssuer
{
    AccessToken IssueAccessToken(AccountId accountId);
}
