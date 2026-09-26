using Level5.Application.Migration;

namespace Level5.LegacyScoreMigration.Reporting;

public enum AuditRowCategory
{
    WillImportCleanly,
    AlreadyLinked,
    // Not "Blocked*" - a pre-existing, payload-identical V2 result (a transition-period row already
    // written by both V1 and a live V2 submission) is not a problem to resolve, just a no-op import
    // that will only attach provenance.
    WillAttachToExistingCompatibleResult,
    BlockedNoAccountLink,
    BlockedMissingOrMalformedScoreid,
    BlockedNonPositiveMode,
    BlockedNonPositiveLevel,
    BlockedCharacterIdNotRepresentable,
    BlockedInvalidVersion,
    BlockedInvalidPlatform,
    BlockedInvalidMetric,
    BlockedConflictingExistingResult
}

/// <summary>
/// <see cref="DateClassification"/> is always computed and reported, but - unlike
/// <see cref="Category"/> - never affects <see cref="AuditSummary.HasBlockers"/>: it is diagnostic
/// information about V1's historically offsetless/client-local <c>Date</c> column, never a reason
/// to block a row (see <see cref="LegacyHighscoreDateClassifier"/>).
/// </summary>
public sealed record AuditRowReport(
    int LegacyHighscoreId, int LegacyUserId, AuditRowCategory Category, string? Detail, LegacyDateClassification DateClassification);

public sealed record AuditSummary(
    int TotalRows,
    IReadOnlyDictionary<AuditRowCategory, int> CountsByCategory,
    IReadOnlyDictionary<LegacyDateClassification, int> CountsByDateClassification)
{
    // Every category an operator must resolve before migrate would cleanly succeed - by naming
    // convention (Blocked*) rather than an enumerated list, so a future added blocker category
    // can't be silently forgotten here the way an explicit OR-chain could be.
    public bool HasBlockers => CountsByCategory
        .Where(kvp => kvp.Key.ToString().StartsWith("Blocked", StringComparison.Ordinal))
        .Any(kvp => kvp.Value > 0);
}
