using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Ids;
using Level5.Domain.Platform;

namespace Level5.Application.Platform;

/// <summary>
/// Trusted provisioning operation. It is intentionally not exposed through an authenticated
/// player controller until a real store-sync or administrative authority exists.
/// </summary>
public sealed record GrantProductEntitlementRequest(
    PlayerId PlayerId,
    ProductId ProductId,
    EntitlementKind Kind,
    DateTimeOffset? ExpiresAt);

public sealed class GrantProductEntitlementUseCase(
    IProductEntitlementStore store,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<ProductEntitlement> ExecuteAsync(
        GrantProductEntitlementRequest request,
        CancellationToken cancellationToken)
    {
        var entitlement = await store.FindAsync(request.PlayerId, request.ProductId, cancellationToken);
        if (entitlement is null)
        {
            entitlement = ProductEntitlement.Grant(
                request.PlayerId,
                request.ProductId,
                request.Kind,
                clock.UtcNow,
                request.ExpiresAt);
            await store.AddAsync(entitlement, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return entitlement;
        }

        var expectedRevision = entitlement.Revision;
        entitlement.Regrant(request.Kind, clock.UtcNow, request.ExpiresAt);
        if (!await store.TrySaveAsync(entitlement, expectedRevision, cancellationToken))
        {
            throw new ConflictException("The entitlement was concurrently modified. Please retry.");
        }

        return entitlement;
    }
}
