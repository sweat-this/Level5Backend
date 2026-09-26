namespace Level5.LegacyAccountMigration.Reporting;

/// <summary>Never a field shaped like a credential - <see cref="AuditRowReport.Detail"/> is human-readable blocker text, never a password or hash.</summary>
public enum AuditRowCategory
{
    WillImportCleanly,
    AlreadyLinked,
    BlockedUsernameCollision,
    BlockedInvalidUsername,
    BlockedInvalidDisplayName,
    // Not "Blocked*": like UnrecognizedCredential, an operator can route around this with an
    // explicit --omit-invalid-email opt-in at migrate time, so it's reported but doesn't fail
    // audit's exit code the way a true (no-escape-hatch) blocker does.
    InvalidEmail,
    EmailCollision,
    UnrecognizedCredential
}

public sealed record AuditRowReport(int LegacyUserId, string LegacyUsername, AuditRowCategory Category, string? Detail);

public sealed record AuditSummary(int TotalRows, IReadOnlyDictionary<AuditRowCategory, int> CountsByCategory)
{
    // Every category an operator must resolve before migrate would cleanly succeed - by naming
    // convention (Blocked*) rather than an enumerated list, so a future added blocker category
    // can't be silently forgotten here the way an explicit OR-chain could be.
    public bool HasBlockers => CountsByCategory
        .Where(kvp => kvp.Key.ToString().StartsWith("Blocked", StringComparison.Ordinal))
        .Any(kvp => kvp.Value > 0);
}
