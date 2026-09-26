namespace Level5.LegacyAccountMigration.Reporting;

public enum VerifyRowStatus
{
    NotMigrated,
    Consistent,
    Inconsistent
}

public sealed record VerifyRowReport(int LegacyUserId, VerifyRowStatus Status, string? Detail);
