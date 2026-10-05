using Level5.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Level5.Infrastructure.Identity;

public sealed class EmailVerificationPolicy(IOptions<EmailVerificationOptions> options) : IEmailVerificationPolicy
{
    public TimeSpan TokenLifetime { get; } = TimeSpan.FromHours(options.Value.TokenLifetimeHours);

    public TimeSpan ResendCooldown { get; } = TimeSpan.FromMinutes(options.Value.ResendCooldownMinutes);
}
