namespace Level5.LegacyAccountMigration.Reporting;

public enum MigrateRowOutcome
{
    Imported,
    AlreadyLinkedConsistent,
    Blocked,
    SkippedUnrecognizedCredential
}

/// <summary>Which credential path a row's Account.PasswordHash came from. Never the credential/hash value itself.</summary>
public enum CredentialPathUsed
{
    NotApplicable,
    RecognizedHashCopied,
    LegacyPlaintextHashed
}

public sealed record MigrateRowReport(int LegacyUserId, MigrateRowOutcome Outcome, string? Detail, CredentialPathUsed CredentialPath);
