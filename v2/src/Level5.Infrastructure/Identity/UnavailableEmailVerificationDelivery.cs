using Level5.Application.Abstractions;
using Level5.Application.Identity;
using Level5.Domain.Identity;

namespace Level5.Infrastructure.Identity;

/// <summary>
/// Safe default until operations configures a real provider adapter. The challenge has already
/// committed when this is called; failing explicitly lets the client offer a later resend.
/// </summary>
public sealed class UnavailableEmailVerificationDelivery : IEmailVerificationDelivery
{
    public Task DeliverAsync(
        Email destination,
        string rawToken,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
        => throw new EmailVerificationDeliveryUnavailableException();
}
