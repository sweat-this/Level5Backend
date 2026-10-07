using Level5.Domain.Identity;

namespace Level5.Application.Abstractions;

/// <summary>
/// Provider-neutral delivery boundary for verified-email replacement. Implementations must never
/// log raw verification credentials. The old-address notification intentionally receives only the
/// previous address, never the replacement value.
/// </summary>
public interface IEmailChangeDelivery
{
    Task DeliverVerificationAsync(
        Email destination,
        string rawToken,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    Task NotifyPreviousAddressAsync(
        Email previousAddress,
        CancellationToken cancellationToken);
}
