using System.ComponentModel.DataAnnotations;

namespace Level5.Infrastructure.Identity;

public sealed class EmailVerificationOptions
{
    public const string SectionName = "EmailVerification";

    [Range(1, 168)]
    public int TokenLifetimeHours { get; init; } = 24;

    [Range(1, 1440)]
    public int ResendCooldownMinutes { get; init; } = 5;
}
