using Level5.Domain.Ids;

namespace Level5.Api.Security;

/// <summary>Resolves the acting player from the authenticated request. HTTP-specific, so it lives in Api, not Application.</summary>
public interface ICurrentPlayerProvider
{
    Task<PlayerId> GetCurrentPlayerIdAsync(CancellationToken cancellationToken);
}
