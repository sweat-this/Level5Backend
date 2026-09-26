using Npgsql;

namespace Level5.LegacyAccountMigration.Safety;

public sealed record SafetyRailResult(bool Passed, string? RefusalReason);

/// <summary>
/// A hard safety rail mirroring Level5.E2E.Fixtures's own database-name allow-list: this tool
/// refuses to run against a V2 database whose name isn't explicitly allow-listed, and refuses to
/// run if the V1 and V2 connection strings resolve to the same database (a copy-paste mistake that
/// would otherwise let the migration write into the wrong database, or read and write the same
/// one). Pure string/NpgsqlConnectionStringBuilder logic - no I/O, independently unit-testable.
/// </summary>
public static class ConnectionStringSafetyRail
{
    public static readonly IReadOnlyCollection<string> DefaultAllowedV2Databases = ["level5_v2", "level5_v2_test"];

    public static SafetyRailResult Check(string v2ConnectionString, string v1ConnectionString, IReadOnlyCollection<string> additionalAllowedV2Databases)
    {
        var v2Database = new NpgsqlConnectionStringBuilder(v2ConnectionString).Database;
        var v1Database = new NpgsqlConnectionStringBuilder(v1ConnectionString).Database;

        var allowList = DefaultAllowedV2Databases.Concat(additionalAllowedV2Databases).ToHashSet(StringComparer.Ordinal);

        if (v2Database is null || !allowList.Contains(v2Database))
        {
            return new SafetyRailResult(false,
                $"Refusing to run: target V2 database is '{v2Database}', which is not in the allow-list " +
                $"({string.Join(", ", allowList)}). Pass --allow-database <name> to explicitly widen this for a one-off run.");
        }

        if (string.Equals(v1Database, v2Database, StringComparison.Ordinal))
        {
            return new SafetyRailResult(false,
                $"Refusing to run: the V1 (legacy) and V2 connection strings both point at database '{v2Database}'. " +
                "This is almost certainly a copy-paste mistake in ConnectionStrings__LegacyDefaultConnection or ConnectionStrings__DefaultConnection.");
        }

        return new SafetyRailResult(true, null);
    }
}
