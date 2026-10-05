using Level5.Application.Abstractions;
using Level5.Domain.Identity;

namespace Level5.Infrastructure.Identity;

public sealed class UnavailablePasswordRecoveryDelivery : IPasswordRecoveryDelivery
{
    public Task<PasswordRecoveryDeliveryOutcome> DeliverAsync(
        Email destination, string rawToken, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        => Task.FromResult(PasswordRecoveryDeliveryOutcome.Unavailable);
}
