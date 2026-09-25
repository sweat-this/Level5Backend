using Level5.Application.Abstractions;
using Level5.Application.Competition;

namespace Level5.Api.BackgroundServices;

/// <summary>
/// Periodically expires challenges that have sat in PendingAcceptance past the configured
/// timeout (Challenges:PendingExpiryDays), via <see cref="ExpireStalePendingChallengesUseCase"/>.
/// No new external infrastructure - just ASP.NET Core's built-in hosted-service model. A failed
/// tick (e.g. a transient database outage) is logged and never crashes the host; the next tick
/// retries.
/// </summary>
public sealed class ChallengeExpirySweepService(
    IServiceScopeFactory scopeFactory, IChallengeExpiryPolicy expiryPolicy, ILogger<ChallengeExpirySweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(expiryPolicy.SweepInterval);

        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Challenge expiry sweep tick failed; will retry on the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var useCase = scope.ServiceProvider.GetRequiredService<ExpireStalePendingChallengesUseCase>();
        var expiredCount = await useCase.ExecuteAsync(cancellationToken);

        if (expiredCount > 0)
        {
            logger.LogInformation("Challenge expiry sweep expired {ExpiredCount} stale pending challenge(s).", expiredCount);
        }
    }
}
