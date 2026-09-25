using Level5.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Level5.Infrastructure.Competition;

/// <summary>Adapts the bound <see cref="ChallengeExpiryOptions"/> configuration to the Application-facing <see cref="IChallengeExpiryPolicy"/> port.</summary>
public sealed class ChallengeExpiryPolicy(IOptions<ChallengeExpiryOptions> options) : IChallengeExpiryPolicy
{
    public TimeSpan PendingAcceptanceTimeout => TimeSpan.FromDays(options.Value.PendingExpiryDays);
    public TimeSpan SweepInterval => TimeSpan.FromMinutes(options.Value.ExpirySweepIntervalMinutes);
    public int SweepBatchSize => options.Value.ExpirySweepBatchSize;
}
