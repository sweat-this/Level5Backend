using Level5.Application.Abstractions;
using Level5.Application.Identity;
using Level5.Domain.Identity;

namespace Level5.Infrastructure.Identity;

/// <summary>
/// Safe default until operations configures transactional email. Development/tests may replace
/// this adapter through DI; production must provide a real implementation before external
/// email-change messages can be delivered.
/// </summary>
public sealed class UnavailableEmailChangeDelivery : IEmailChangeDelivery
{
    public Task DeliverVerificationAsync(
        Email destination,
        string rawToken,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
        => throw new EmailChangeDeliveryUnavailableException();

    public Task NotifyPreviousAddressAsync(
        Email previousAddress,
        CancellationToken cancellationToken)
        => throw new EmailChangeDeliveryUnavailableException();
}
