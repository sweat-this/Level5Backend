using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Ids;
using Level5.Domain.Platform;

namespace Level5.Application.Platform;

/// <summary>
/// Trusted provisioning operation. Missing/already-revoked records are successful no-ops, and no
/// player-facing mutation route maps to this use case.
/// </summary>
public sealed record RevokeProductEntitlementRequest(PlayerId PlayerId, ProductId ProductId);

public sealed class RevokeProductEntitlementUseCase(IProductEntitlementStore store, IClock clock)
{
    public async Task ExecuteAsync(
        RevokeProductEntitlementRequest request,
        CancellationToken cancellationToken)
    {
        var entitlement = await store.FindAsync(request.PlayerId, request.ProductId, cancellationToken);
        if (entitlement is null || entitlement.RevokedAt is not null)
        {
            return;
        }

        var expectedRevision = entitlement.Revision;
        entitlement.Revoke(clock.UtcNow);
        if (await store.TrySaveAsync(entitlement, expectedRevision, cancellationToken))
        {
            return;
        }

        // Concurrent identical revocation is idempotent. A concurrent regrant remains a conflict
        // instead of being silently overwritten by this stale command.
        var current = await store.FindAsync(request.PlayerId, request.ProductId, cancellationToken);
        if (current is null || current.RevokedAt is not null)
        {
            return;
        }

        throw new ConflictException("The entitlement was concurrently modified. Please retry.");
    }
}
