using Level5.Application.Abstractions;
using Level5.Application.Migration;
using Level5.Domain.Identity;
using Level5.LegacyAccountMigration.Legacy;
using Level5.LegacyAccountMigration.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace Level5.LegacyAccountMigration.Commands;

public sealed record AuditOptions(int? LegacyUserId, int? Limit, bool Verbose);

/// <summary>
/// Read-only report: never writes to V2, never touches V1 beyond a SELECT. Evaluates each legacy
/// row in the same order <see cref="Commands.MigrateCommand"/> would actually process it, so the
/// summary is a faithful prediction of what a real `migrate` run would do - in particular, an
/// unrecognized credential is reported before username/collision/display-name checks, because
/// `migrate` skips those rows before ever calling the import use case unless
/// --accept-legacy-plaintext is set.
/// </summary>
public static class AuditCommand
{
    public static async Task<int> RunAsync(LegacyUserReader legacyReader, IServiceProvider provider, AuditOptions options, CancellationToken cancellationToken)
    {
        var rows = new List<AuditRowReport>();

        await foreach (var record in ReadRecordsAsync(legacyReader, options, cancellationToken))
        {
            await using var scope = provider.CreateAsyncScope();
            var linkStore = scope.ServiceProvider.GetRequiredService<ILegacyAccountLinkStore>();
            var accountStore = scope.ServiceProvider.GetRequiredService<IAccountStore>();

            rows.Add(await EvaluateAsync(record, linkStore, accountStore, cancellationToken));
        }

        var summary = Summarize(rows);
        PrintSummary(summary);
        if (options.Verbose)
        {
            PrintVerbose(rows);
        }

        return summary.HasBlockers ? 1 : 0;
    }

    private static async IAsyncEnumerable<LegacyUserRecord> ReadRecordsAsync(
        LegacyUserReader legacyReader,
        AuditOptions options,
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

    private static async Task<AuditRowReport> EvaluateAsync(
        LegacyUserRecord record,
        ILegacyAccountLinkStore linkStore,
        IAccountStore accountStore,
        CancellationToken cancellationToken)
    {
        if (await linkStore.ExistsByLegacyUserIdAsync(record.UserId, cancellationToken))
        {
            return new AuditRowReport(record.UserId, record.Username, AuditRowCategory.AlreadyLinked, null);
        }

        // Mirrors MigrateCommand's actual order: an unrecognized credential is skipped before the
        // import use case ever validates username/collision/display-name, unless the operator
        // opts in with --accept-legacy-plaintext - so report it first, not last.
        if (LegacyCredentialClassifier.TryClassify(record.Password).Kind == LegacyCredentialKind.UnrecognizedLegacyCredential)
        {
            return new AuditRowReport(record.UserId, record.Username, AuditRowCategory.UnrecognizedCredential, null);
        }

        Username username;
        try
        {
            username = Username.Create(record.Username);
        }
        catch (InvalidUsernameException ex)
        {
            return new AuditRowReport(record.UserId, record.Username, AuditRowCategory.BlockedInvalidUsername, ex.Message);
        }

        var existingAccount = await accountStore.FindByUsernameAsync(username, cancellationToken);
        if (existingAccount is not null)
        {
            // Same distinction ImportLegacyAccountUseCase makes: a collision with an account that
            // was itself already migrated (a likely V1 case-sensitive-vs-V2 case-insensitive
            // duplicate) gets a different, more actionable message than an unrelated V2 signup.
            var collidesWithMigratedAccount = await linkStore.ExistsForAccountIdAsync(existingAccount.Id, cancellationToken);
            return new AuditRowReport(record.UserId, record.Username, AuditRowCategory.BlockedUsernameCollision, collidesWithMigratedAccount
                ? $"Username '{username.Value}' already exists in V2, migrated there from a different legacy user. Likely a case-variant collision."
                : $"Username '{username.Value}' already exists in V2 with no legacy_account_links record. Refusing to auto-link.");
        }

        // Same rule as PlayerProfile.ValidateDisplayName - legacy username is the default
        // display-name candidate (see ImportLegacyAccountUseCase), never auto-truncated/renamed.
        var displayNameCandidate = record.Username.Trim();
        if (displayNameCandidate.Length is 0 or > 32)
        {
            return new AuditRowReport(record.UserId, record.Username, AuditRowCategory.BlockedInvalidDisplayName,
                "Legacy username is not a valid V2 display name (empty after trim, or exceeds 32 characters).");
        }

        if (!string.IsNullOrWhiteSpace(record.Email))
        {
            Email candidateEmail;
            try
            {
                candidateEmail = Email.Create(record.Email);
            }
            catch (InvalidEmailException ex)
            {
                return new AuditRowReport(record.UserId, record.Username, AuditRowCategory.InvalidEmail, ex.Message);
            }

            if (await accountStore.EmailExistsAsync(candidateEmail, cancellationToken))
            {
                return new AuditRowReport(record.UserId, record.Username, AuditRowCategory.EmailCollision,
                    "Legacy email already exists in V2 (canonical conflict). Refusing to auto-link.");
            }
        }

        return new AuditRowReport(record.UserId, record.Username, AuditRowCategory.WillImportCleanly, null);
    }

    private static AuditSummary Summarize(IReadOnlyList<AuditRowReport> rows)
    {
        var counts = Enum.GetValues<AuditRowCategory>().ToDictionary(c => c, _ => 0);
        foreach (var row in rows)
        {
            counts[row.Category]++;
        }

        return new AuditSummary(rows.Count, counts);
    }

    private static void PrintSummary(AuditSummary summary)
    {
        Console.WriteLine($"Audited {summary.TotalRows} legacy user(s):");
        foreach (var (category, count) in summary.CountsByCategory)
        {
            Console.WriteLine($"  {category}: {count}");
        }
    }

    private static void PrintVerbose(IReadOnlyList<AuditRowReport> rows)
    {
        foreach (var row in rows.Where(r => r.Category is not (AuditRowCategory.WillImportCleanly or AuditRowCategory.AlreadyLinked)))
        {
            var detail = row.Detail is null ? string.Empty : $": {row.Detail}";
            Console.WriteLine($"  [{row.Category}] legacy user {row.LegacyUserId} ('{row.LegacyUsername}'){detail}");
        }
    }
}
