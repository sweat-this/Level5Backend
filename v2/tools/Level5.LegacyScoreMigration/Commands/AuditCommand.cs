using Level5.Application.Abstractions;
using Level5.Application.Migration;
using Level5.LegacyScoreMigration.Legacy;
using Level5.LegacyScoreMigration.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace Level5.LegacyScoreMigration.Commands;

public sealed record AuditOptions(int? LegacyHighscoreId, int? Limit, bool Verbose);

/// <summary>
/// Read-only report: never writes to V2, never touches V1 beyond a SELECT. Evaluates each legacy
/// row in the same order <see cref="Commands.MigrateCommand"/> would actually process it (existing
/// link -&gt; account link -&gt; canonical-mapping blockers, in <see cref="LegacyHighscoreMapper"/>'s
/// own field precedence -&gt; existing-V2-result reconciliation -&gt; clean import), so the summary is
/// a faithful prediction of what a real <c>migrate</c> run would do.
/// </summary>
public static class AuditCommand
{
    public static async Task<int> RunAsync(LegacyHighscoreReader legacyReader, IServiceProvider provider, AuditOptions options, CancellationToken cancellationToken)
    {
        var rows = new List<AuditRowReport>();

        await foreach (var record in ReadRecordsAsync(legacyReader, options, cancellationToken))
        {
            await using var scope = provider.CreateAsyncScope();
            var accountLinkStore = scope.ServiceProvider.GetRequiredService<ILegacyAccountLinkStore>();
            var matchResultLinkStore = scope.ServiceProvider.GetRequiredService<ILegacyMatchResultLinkStore>();
            var matchResultStore = scope.ServiceProvider.GetRequiredService<IMatchResultStore>();
            var leaderboardPolicyCatalog = scope.ServiceProvider.GetRequiredService<ILeaderboardPolicyCatalog>();

            rows.Add(await EvaluateAsync(record, accountLinkStore, matchResultLinkStore, matchResultStore, leaderboardPolicyCatalog, cancellationToken));
        }

        var summary = Summarize(rows);
        PrintSummary(summary);
        if (options.Verbose)
        {
            PrintVerbose(rows);
        }

        return summary.HasBlockers ? 1 : 0;
    }

    private static async IAsyncEnumerable<LegacyHighscoreRecord> ReadRecordsAsync(
        LegacyHighscoreReader legacyReader,
        AuditOptions options,
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

    private static async Task<AuditRowReport> EvaluateAsync(
        LegacyHighscoreRecord record,
        ILegacyAccountLinkStore accountLinkStore,
        ILegacyMatchResultLinkStore matchResultLinkStore,
        IMatchResultStore matchResultStore,
        ILeaderboardPolicyCatalog leaderboardPolicyCatalog,
        CancellationToken cancellationToken)
    {
        var dateClassification = LegacyHighscoreDateClassifier.Classify(record.Date);

        if (await matchResultLinkStore.FindByLegacyHighscoreIdAsync(record.Id, cancellationToken) is not null)
        {
            return Report(record, AuditRowCategory.AlreadyLinked, null, dateClassification);
        }

        var accountLink = await accountLinkStore.FindByLegacyUserIdAsync(record.Userid, cancellationToken);
        if (accountLink is null)
        {
            return Report(record, AuditRowCategory.BlockedNoAccountLink,
                $"No legacy_account_links row exists for legacy user {record.Userid}.", dateClassification);
        }

        var mapping = LegacyHighscoreMapper.TryMap(record.ToMappingInput());
        if (!mapping.IsValid)
        {
            return Report(record, ToCategory(mapping.Blocker), mapping.BlockDetail, dateClassification);
        }

        var existingResult = await matchResultStore.FindByClientResultIdAsync(accountLink.PlayerId, mapping.ClientResultId, cancellationToken);
        if (existingResult is not null)
        {
            var sameRequest = existingResult.MatchesRequest(
                mapping.ModeId, mapping.LevelId, mapping.CharacterId!, mapping.ClientVersion!, mapping.Platform!, mapping.Metrics!, mapping.Modifiers!);

            return sameRequest
                ? Report(record, AuditRowCategory.WillAttachToExistingCompatibleResult, null, dateClassification)
                : Report(record, AuditRowCategory.BlockedConflictingExistingResult,
                    $"An existing V2 match result ({existingResult.Id}) for this player/clientResultId has different material fields.", dateClassification);
        }

        return Report(record, AuditRowCategory.WillImportCleanly, null, dateClassification);
    }

    private static AuditRowReport Report(LegacyHighscoreRecord record, AuditRowCategory category, string? detail, LegacyDateClassification dateClassification)
        => new(record.Id, record.Userid, category, detail, dateClassification);

    private static AuditRowCategory ToCategory(LegacyHighscoreMappingBlocker blocker) => blocker switch
    {
        LegacyHighscoreMappingBlocker.MissingOrMalformedScoreid => AuditRowCategory.BlockedMissingOrMalformedScoreid,
        LegacyHighscoreMappingBlocker.NonPositiveMode => AuditRowCategory.BlockedNonPositiveMode,
        LegacyHighscoreMappingBlocker.NonPositiveLevel => AuditRowCategory.BlockedNonPositiveLevel,
        LegacyHighscoreMappingBlocker.CharacterIdNotRepresentable => AuditRowCategory.BlockedCharacterIdNotRepresentable,
        LegacyHighscoreMappingBlocker.InvalidVersion => AuditRowCategory.BlockedInvalidVersion,
        LegacyHighscoreMappingBlocker.InvalidPlatform => AuditRowCategory.BlockedInvalidPlatform,
        LegacyHighscoreMappingBlocker.InvalidMetricValue => AuditRowCategory.BlockedInvalidMetric,
        _ => throw new InvalidOperationException($"Unhandled mapping blocker: {blocker}")
    };

    private static AuditSummary Summarize(IReadOnlyList<AuditRowReport> rows)
    {
        var categoryCounts = Enum.GetValues<AuditRowCategory>().ToDictionary(c => c, _ => 0);
        var dateCounts = Enum.GetValues<LegacyDateClassification>().ToDictionary(c => c, _ => 0);
        foreach (var row in rows)
        {
            categoryCounts[row.Category]++;
            dateCounts[row.DateClassification]++;
        }

        return new AuditSummary(rows.Count, categoryCounts, dateCounts);
    }

    private static void PrintSummary(AuditSummary summary)
    {
        Console.WriteLine($"Audited {summary.TotalRows} legacy highscore(s):");
        foreach (var (category, count) in summary.CountsByCategory)
        {
            Console.WriteLine($"  {category}: {count}");
        }

        Console.WriteLine("Date classification (diagnostic only - never blocks a row):");
        foreach (var (classification, count) in summary.CountsByDateClassification)
        {
            Console.WriteLine($"  {classification}: {count}");
        }
    }

    private static void PrintVerbose(IReadOnlyList<AuditRowReport> rows)
    {
        foreach (var row in rows.Where(r => r.Category is not (AuditRowCategory.WillImportCleanly or AuditRowCategory.AlreadyLinked or AuditRowCategory.WillAttachToExistingCompatibleResult)))
        {
            var detail = row.Detail is null ? string.Empty : $": {row.Detail}";
            Console.WriteLine($"  [{row.Category}] legacy highscore {row.LegacyHighscoreId} (legacy user {row.LegacyUserId}){detail}");
        }
    }
}
