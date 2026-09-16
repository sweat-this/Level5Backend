using Level5.Domain.Ids;

namespace Level5.Api.Security;

/// <summary>Resolves the authenticated <see cref="AccountId"/> from the current request's JWT `sub` claim. HTTP-specific, so it lives in Api, not Application.</summary>
public interface ICurrentAccountAccessor
{
    AccountId GetCurrentAccountId();
}
