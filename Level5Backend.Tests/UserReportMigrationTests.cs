using System.Reflection;
using Level5Backend.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Level5Backend.Tests;

public sealed class UserReportMigrationTests
{
    [Fact]
    public void AttributionRollback_BackfillsAnonymousRowsBeforeRestoringNotNullConstraints()
    {
        var migration = new MakeUserReportAttributionNullable();
        var migrationBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        MethodInfo down = typeof(MakeUserReportAttributionNullable).GetMethod(
            "Down",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        down.Invoke(migration, [migrationBuilder]);

        var backfill = Assert.IsType<SqlOperation>(migrationBuilder.Operations[0]);
        Assert.Contains("SET \"userid\" = 999", backfill.Sql, StringComparison.Ordinal);
        Assert.Contains("\"userName\" = 'not logged in'", backfill.Sql, StringComparison.Ordinal);

        var restoredColumns = migrationBuilder.Operations
            .OfType<AlterColumnOperation>()
            .ToList();
        Assert.Equal(2, restoredColumns.Count);
        Assert.All(restoredColumns, column => Assert.False(column.IsNullable));
    }
}
