using Level5.Application.Abstractions;
using Level5.Application.Migration;
using Level5.LegacyAccountMigration.Legacy;
using Level5.LegacyAccountMigration.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace Level5.LegacyAccountMigration.Commands;

public sealed record VerifyOptions(int? LegacyUserId);

/// <summary>
/// Read-only post-check. Runs the same LegacyLinkConsistencyChecker that
/// ImportLegacyAccountUseCase's idempotent-resume path uses, so "consistent" can never mean
/// something different here than it does during a live migrate run. Collects every row's result
/// rather than stopping at the first problem - verify's whole job is to surface every issue in one
/// pass.
/// </summary>
public static class VerifyCommand
{
    public static async Task<int> RunAsync(LegacyUserReader legacyReader, IServiceProvider provider, VerifyOptions options, CancellationToken cancellationToken)
    {
        var reports = new List<VerifyRowReport>();

        await foreach (var record in ReadRecordsAsync(legacyReader, options, cancellationToken))
        {
            await using var scope = provider.CreateAsyncScope();
            var linkStore = scope.ServiceProvider.GetRequiredService<ILegacyAccountLinkStore>();

            var link = await linkStore.FindByLegacyUserIdAsync(record.UserId, cancellationToken);
            if (link is null)
            {
                reports.Add(new VerifyRowReport(record.UserId, VerifyRowStatus.NotMigrated, null));
                continue;
            }

            var accountStore = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            var playerProfileStore = scope.ServiceProvider.GetRequiredService<IPlayerProfileStore>();
            var result = await LegacyLinkConsistencyChecker.CheckAsync(link, accountStore, playerProfileStore, cancellationToken);

            reports.Add(result.IsConsistent
                ? new VerifyRowReport(record.UserId, VerifyRowStatus.Consistent, null)
                : new VerifyRowReport(record.UserId, VerifyRowStatus.Inconsistent, result.Reason));
        }

        PrintSummary(reports);

        var inconsistentCount = reports.Count(r => r.Status == VerifyRowStatus.Inconsistent);
        return inconsistentCount == 0 ? 0 : 1;
    }

    private static async IAsyncEnumerable<LegacyUserRecord> ReadRecordsAsync(
        LegacyUserReader legacyReader,
        VerifyOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (options.LegacyUserId is { } id)
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
        Console.WriteLine($"Verified {reports.Count} legacy user(s):");
        foreach (var group in reports.GroupBy(r => r.Status))
        {
            Console.WriteLine($"  {group.Key}: {group.Count()}");
        }

        foreach (var report in reports.Where(r => r.Status == VerifyRowStatus.Inconsistent))
        {
            Console.WriteLine($"  [Inconsistent] legacy user {report.LegacyUserId}: {report.Detail}");
        }
    }
}
