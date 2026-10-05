using System.ComponentModel.DataAnnotations;

namespace Level5.Infrastructure.Identity;

public sealed class PasswordRecoveryOptions
{
    public const string SectionName = "PasswordRecovery";
    [Range(1, 12)] public int TokenLifetimeHours { get; init; } = 1;
    [Range(1, 1440)] public int RequestCooldownMinutes { get; init; } = 5;
}
