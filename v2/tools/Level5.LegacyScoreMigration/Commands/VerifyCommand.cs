using Level5.Application.Abstractions;
using Level5.Application.Migration;
using Level5.LegacyScoreMigration.Legacy;
using Level5.LegacyScoreMigration.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace Level5.LegacyScoreMigration.Commands;

public sealed record VerifyOptions(int? LegacyHighscoreId);

/// <summary>
/// Read-only post-check. Runs the same <see cref="LegacyMatchResultLinkConsistencyChecker"/> that
/// <see cref="ImportLegacyMatchResultUseCase"/>'s idempotent-resume path uses, so "consistent" can
/// never mean something different here than it does during a live migrate run. Collects every row's
/// result rather than stopping at the first problem - verify's whole job is to surface every issue
/// in one pass.
/// </summary>
public static class VerifyCommand
{
    public static async Task<int> RunAsync(LegacyHighscoreReader legacyReader, IServiceProvider provider, VerifyOptions options, CancellationToken cancellationToken)
    {
        var reports = new List<VerifyRowReport>();

        await foreach (var record in ReadRecordsAsync(legacyReader, options, cancellationToken))
        {
            await using var scope = provider.CreateAsyncScope();
            var matchResultLinkStore = scope.ServiceProvider.GetRequiredService<ILegacyMatchResultLinkStore>();

            var link = await matchResultLinkStore.FindByLegacyHighscoreIdAsync(record.Id, cancellationToken);
            if (link is null)
            {
                reports.Add(new VerifyRowReport(record.Id, VerifyRowStatus.NotMigrated, null));
                continue;
            }

            var matchResultStore = scope.ServiceProvider.GetRequiredService<IMatchResultStore>();
            var accountLinkStore = scope.ServiceProvider.GetRequiredService<ILegacyAccountLinkStore>();
            var leaderboardPolicyCatalog = scope.ServiceProvider.GetRequiredService<ILeaderboardPolicyCatalog>();

            var result = await LegacyMatchResultLinkConsistencyChecker.CheckAsync(
                link, record.Userid, record.ToMappingInput(), matchResultStore, accountLinkStore, leaderboardPolicyCatalog, cancellationToken);

            reports.Add(result.IsConsistent
                ? new VerifyRowReport(record.Id, VerifyRowStatus.Consistent, null)
                : new VerifyRowReport(record.Id, VerifyRowStatus.Inconsistent, result.Reason));
        }

        PrintSummary(reports);

        var inconsistentCount = reports.Count(r => r.Status == VerifyRowStatus.Inconsistent);
        return inconsistentCount == 0 ? 0 : 1;
    }

    private static async IAsyncEnumerable<LegacyHighscoreRecord> ReadRecordsAsync(
        LegacyHighscoreReader legacyReader,
        VerifyOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (options.LegacyHighscoreId is { } id)
        {
            var record = await legacyReader.ReadOneAsync(id, cancellationToken);
            if (record is not null)
            {
                yield return record;
            }

            yield break;
        }

        await foreach (var record in legacyReader.ReadAllAsync(cancellationToken))
        {
            yield return record;
        }
    }

    private static void PrintSummary(IReadOnlyList<VerifyRowReport> reports)
    {
        Console.WriteLine($"Verified {reports.Count} legacy highscore(s):");
        foreach (var group in reports.GroupBy(r => r.Status))
        {
            Console.WriteLine($"  {group.Key}: {group.Count()}");
        }

        foreach (var report in reports.Where(r => r.Status == VerifyRowStatus.Inconsistent))
        {
            Console.WriteLine($"  [Inconsistent] legacy highscore {report.LegacyHighscoreId}: {report.Detail}");
        }
    }
}
