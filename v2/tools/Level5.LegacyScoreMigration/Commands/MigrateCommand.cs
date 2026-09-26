using Level5.Application.Common;
using Level5.Application.Migration;
using Level5.LegacyScoreMigration.Legacy;
using Level5.LegacyScoreMigration.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace Level5.LegacyScoreMigration.Commands;

public sealed record MigrateOptions(int? LegacyHighscoreId, int? Limit);

/// <summary>
/// Writes V2 <c>match_results</c>/<c>legacy_match_result_links</c> rows. Each legacy row gets its
/// own <see cref="IServiceScope"/>/DbContext/transaction (via
/// <see cref="ImportLegacyMatchResultUseCase"/>) - one row's failure never touches another row's
/// already-committed state, and the run is always safe to resume after an interruption. A
/// <see cref="LegacyMigrationInconsistentException"/> is never caught-and-continued: it signals real
/// data corruption and aborts the entire run (exit code 2), since continuing could compound the
/// inconsistency across more rows. Every other per-row outcome (imported, attached, already linked,
/// blocked) is logged and the run continues.
///
/// <see cref="ImportLegacyMatchResultUseCase"/> already absorbs the ordinary concurrent-migrate race
/// on a single row internally (reloading and resolving whoever actually committed), but a
/// <see cref="ConflictException"/> can still surface here in the rare case where a conflict isn't
/// explained by the key it just reloaded by - reported like any other blocked row rather than
/// crashing the run, mirroring <c>Level5.LegacyAccountMigration.Commands.MigrateCommand</c>'s own
/// handling of the identical class of race.
/// </summary>
public static class MigrateCommand
{
    public static async Task<int> RunAsync(LegacyHighscoreReader legacyReader, IServiceProvider provider, MigrateOptions options, CancellationToken cancellationToken)
    {
        var reports = new List<MigrateRowReport>();

        await foreach (var record in ReadRecordsAsync(legacyReader, options, cancellationToken))
        {
            await using var scope = provider.CreateAsyncScope();
            var useCase = scope.ServiceProvider.GetRequiredService<ImportLegacyMatchResultUseCase>();

            ImportLegacyMatchResultResult result;
            try
            {
                result = await useCase.ExecuteAsync(
                    new ImportLegacyMatchResultRequest(record.Id, record.Userid, record.ToMappingInput()), cancellationToken);
            }
            catch (LegacyMigrationInconsistentException ex)
            {
                await Console.Error.WriteLineAsync($"ABORTING migrate run: {ex.Message}");
                PrintSummary(reports);
                return 2;
            }
            catch (ConflictException ex)
            {
                reports.Add(new MigrateRowReport(record.Id, MigrateRowOutcome.Blocked, $"Conflicted with concurrently-written V2 data: {ex.Message}"));
                continue;
            }

            var outcome = result.Outcome switch
            {
                ImportLegacyMatchResultOutcome.Imported => MigrateRowOutcome.Imported,
                ImportLegacyMatchResultOutcome.AttachedToExistingCompatibleResult => MigrateRowOutcome.AttachedToExistingCompatibleResult,
                ImportLegacyMatchResultOutcome.AlreadyLinkedConsistent => MigrateRowOutcome.AlreadyLinkedConsistent,
                ImportLegacyMatchResultOutcome.Blocked => MigrateRowOutcome.Blocked,
                _ => throw new InvalidOperationException($"Unhandled outcome: {result.Outcome}")
            };

            reports.Add(new MigrateRowReport(record.Id, outcome, result.BlockReason));
        }

        PrintSummary(reports);
        return 0;
    }

    private static async IAsyncEnumerable<LegacyHighscoreRecord> ReadRecordsAsync(
        LegacyHighscoreReader legacyReader,
        MigrateOptions options,
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

        var count = 0;
        await foreach (var record in legacyReader.ReadAllAsync(cancellationToken))
        {
            if (options.Limit is { } limit && count >= limit)
            {
                yield break;
            }

            count++;
            yield return record;
        }
    }

    private static void PrintSummary(IReadOnlyList<MigrateRowReport> reports)
    {
        Console.WriteLine($"Migrated {reports.Count} legacy highscore(s):");
        foreach (var group in reports.GroupBy(r => r.Outcome))
        {
            Console.WriteLine($"  {group.Key}: {group.Count()}");
        }

        foreach (var report in reports.Where(r => r.Outcome == MigrateRowOutcome.Blocked))
        {
            Console.WriteLine($"  [Blocked] legacy highscore {report.LegacyHighscoreId}: {report.Detail}");
        }
    }
}
