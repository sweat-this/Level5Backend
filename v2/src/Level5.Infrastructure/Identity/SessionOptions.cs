using System.ComponentModel.DataAnnotations;

namespace Level5.Infrastructure.Identity;

/// <summary>
/// Validated at startup via AddOptions().ValidateDataAnnotations().ValidateOnStart() - see
/// ServiceCollectionExtensions - so a misconfigured session lifetime fails the host immediately
/// rather than on the first login attempt.
/// </summary>
public sealed class SessionOptions
{
    public const string SectionName = "Sessions";

    [Range(1, 365)]
    public int RefreshTokenLifetimeDays { get; set; } = 30;
}
