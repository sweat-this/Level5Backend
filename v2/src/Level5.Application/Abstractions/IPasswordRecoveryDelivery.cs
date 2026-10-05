using Level5.Domain.Identity;

namespace Level5.Application.Abstractions;

public enum PasswordRecoveryDeliveryOutcome { Delivered, Unavailable }

public interface IPasswordRecoveryDelivery
{
    /// <summary>
    /// Attempts delivery without exposing provider availability to the caller. Implementations
    /// should return <see cref="PasswordRecoveryDeliveryOutcome.Unavailable"/> for expected
    /// provider failures; the recovery use case also guards unexpected faults so the anonymous
    /// HTTP contract remains enumeration-safe.
    /// </summary>
    Task<PasswordRecoveryDeliveryOutcome> DeliverAsync(
        Email destination, string rawToken, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}
