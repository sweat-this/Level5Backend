using Level5.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Level5.Infrastructure.Identity;

public sealed class PasswordRecoveryPolicy(IOptions<PasswordRecoveryOptions> options) : IPasswordRecoveryPolicy
{
    public TimeSpan TokenLifetime { get; } = TimeSpan.FromHours(options.Value.TokenLifetimeHours);
    public TimeSpan RequestCooldown { get; } = TimeSpan.FromMinutes(options.Value.RequestCooldownMinutes);
}
