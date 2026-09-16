using Level5.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Level5.Infrastructure.Identity;

/// <summary>Adapts the bound <see cref="SessionOptions"/> configuration to the Application-facing <see cref="IAuthSessionPolicy"/> port.</summary>
public sealed class AuthSessionPolicy(IOptions<SessionOptions> options) : IAuthSessionPolicy
{
    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(options.Value.RefreshTokenLifetimeDays);
}
