using System.ComponentModel.DataAnnotations;

namespace Level5.Infrastructure.Competition;

/// <summary>
/// Validated at startup via AddOptions().ValidateDataAnnotations().ValidateOnStart() - see
/// ServiceCollectionExtensions - so a misconfigured expiry timeout fails the host immediately
/// rather than silently never expiring anything.
/// </summary>
public sealed class ChallengeExpiryOptions
{
    public const string SectionName = "Challenges";

    /// <summary>How long a challenge may sit in PendingAcceptance before the background sweep expires it.</summary>
    [Range(1, 365)]
    public int PendingExpiryDays { get; set; } = 30;

    /// <summary>How often the background sweep runs.</summary>
    [Range(1, 1440)]
    public int ExpirySweepIntervalMinutes { get; set; } = 60;

    /// <summary>Upper bound on how many stale challenges one sweep tick will expire.</summary>
    [Range(1, 10000)]
    public int ExpirySweepBatchSize { get; set; } = 100;
}
