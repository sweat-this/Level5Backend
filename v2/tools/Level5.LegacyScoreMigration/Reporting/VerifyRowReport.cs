namespace Level5.LegacyScoreMigration.Reporting;

public enum VerifyRowStatus
{
    NotMigrated,
    Consistent,
    Inconsistent
}

public sealed record VerifyRowReport(int LegacyHighscoreId, VerifyRowStatus Status, string? Detail);
