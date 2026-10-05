using Level5.Domain.Identity;

namespace Level5.Application.Abstractions;

/// <summary>Provider-neutral outbound delivery boundary. Implementations must never log the raw token.</summary>
public interface IEmailVerificationDelivery
{
    Task DeliverAsync(
        Email destination,
        string rawToken,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);
}
