namespace Level5.LegacyScoreMigration.Reporting;

public enum MigrateRowOutcome
{
    Imported,
    AttachedToExistingCompatibleResult,
    AlreadyLinkedConsistent,
    Blocked
}

public sealed record MigrateRowReport(int LegacyHighscoreId, MigrateRowOutcome Outcome, string? Detail);
