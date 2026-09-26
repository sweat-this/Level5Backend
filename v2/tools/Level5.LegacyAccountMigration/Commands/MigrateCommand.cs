using Level5.Application.Common;
using Level5.Application.Migration;
using Level5.LegacyAccountMigration.Legacy;
using Level5.LegacyAccountMigration.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace Level5.LegacyAccountMigration.Commands;

public sealed record MigrateOptions(bool AcceptLegacyPlaintext, int? LegacyUserId, int? Limit, bool OmitInvalidEmail = false);

/// <summary>
/// Writes V2 Account/PlayerProfile/legacy_account_links rows. Each legacy row gets its own
/// IServiceScope/DbContext/SaveChangesAsync - one row's failure never touches another row's
/// already-committed state. A LegacyMigrationInconsistentException is never caught-and-continued:
/// it signals real data corruption and aborts the entire run (exit code 2), since continuing could
/// compound the inconsistency across more rows. Every other per-row outcome (imported, already
/// linked, blocked, skipped) is logged and the run continues.
/// </summary>
public static class MigrateCommand
{
    public static async Task<int> RunAsync(LegacyUserReader legacyReader, IServiceProvider provider, MigrateOptions options, CancellationToken cancellationToken)
    {
        var reports = new List<MigrateRowReport>();

        await foreach (var record in ReadRecordsAsync(legacyReader, options, cancellationToken))
        {
            var classification = LegacyCredentialClassifier.TryClassify(record.Password);

            if (classification.Kind == LegacyCredentialKind.UnrecognizedLegacyCredential && !options.AcceptLegacyPlaintext)
            {
                reports.Add(new MigrateRowReport(record.UserId, MigrateRowOutcome.SkippedUnrecognizedCredential, null, CredentialPathUsed.NotApplicable));
                continue;
            }

            await using var scope = provider.CreateAsyncScope();

            // The credential is classified here (never logged/stored), but hashing a
            // --accept-legacy-plaintext value is deferred to ImportLegacyAccountUseCase itself,
            // which only actually calls IPasswordHasher.Hash once it knows the row is really
            // about to be imported (see that method's comment) - an already-migrated or
            // newly-blocked row never pays the PBKDF2 cost on a rerun.
            var credential = classification.Kind == LegacyCredentialKind.RecognizedHash
                ? LegacyCredential.AlreadyHashed(record.Password)
                : LegacyCredential.Plaintext(record.Password);
            var credentialPath = classification.Kind == LegacyCredentialKind.RecognizedHash
                ? CredentialPathUsed.RecognizedHashCopied
                : CredentialPathUsed.LegacyPlaintextHashed;

            var useCase = scope.ServiceProvider.GetRequiredService<ImportLegacyAccountUseCase>();

            ImportLegacyAccountResult result;
            try
            {
                result = await useCase.ExecuteAsync(
                    new ImportLegacyAccountRequest(
                        record.UserId, record.Username, credential, record.Username,
                        record.Email, options.OmitInvalidEmail),
                    cancellationToken);
            }
            catch (LegacyMigrationInconsistentException ex)
            {
                await Console.Error.WriteLineAsync($"ABORTING migrate run: {ex.Message}");
                PrintSummary(reports);
                return 2;
            }
            catch (ConflictException ex)
            {
                // A unique-index race: something else (most plausibly a concurrent live
                // registration, since migrate is documented to run before public registration is
                // enabled - see the README's rollout order) inserted a row with the same
                // username/email/tag between this row's pre-check and its SaveChangesAsync. This
                // is reported like any other blocked row and the run continues - it is not data
                // corruption (nothing partially committed; EfUnitOfWork's conflict translation
                // means SaveChangesAsync either fully applies or throws before writing anything),
                // just a genuine, retriable collision discovered slightly later than usual.
                reports.Add(new MigrateRowReport(record.UserId, MigrateRowOutcome.Blocked,
                    $"Conflicted with concurrently-written V2 data: {ex.Message}", credentialPath));
                continue;
            }

            var outcome = result.Outcome switch
            {
                ImportLegacyAccountOutcome.Imported => MigrateRowOutcome.Imported,
                ImportLegacyAccountOutcome.AlreadyLinkedConsistent => MigrateRowOutcome.AlreadyLinkedConsistent,
                ImportLegacyAccountOutcome.Blocked => MigrateRowOutcome.Blocked,
                _ => throw new InvalidOperationException($"Unhandled outcome: {result.Outcome}")
            };

            reports.Add(new MigrateRowReport(record.UserId, outcome, result.BlockReason, credentialPath));
        }

        PrintSummary(reports);
        return 0;
    }

    private static async IAsyncEnumerable<LegacyUserRecord> ReadRecordsAsync(
        LegacyUserReader legacyReader,
        MigrateOptions options,
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
        Console.WriteLine($"Migrated {reports.Count} legacy user(s):");
        foreach (var group in reports.GroupBy(r => r.Outcome))
        {
            Console.WriteLine($"  {group.Key}: {group.Count()}");
        }

        foreach (var report in reports.Where(r => r.Outcome == MigrateRowOutcome.Blocked))
        {
            Console.WriteLine($"  [Blocked] legacy user {report.LegacyUserId}: {report.Detail}");
        }
    }
}
