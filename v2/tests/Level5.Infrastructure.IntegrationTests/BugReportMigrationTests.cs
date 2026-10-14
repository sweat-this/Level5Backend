using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

namespace Level5.Infrastructure.IntegrationTests;

public sealed class BugReportMigrationTests
{
    [Fact]
    public async Task Upgrade_adds_only_reports_preserves_players_and_has_no_model_drift()
    {
        await using var container = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("bug_report_upgrade").WithUsername("level5").WithPassword("test-password").Build();
        await container.StartAsync();
        await using var db = new Level5V2DbContext(new DbContextOptionsBuilder<Level5V2DbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261010183439_AddBloodMoneyChatReports");
        var player = await PlayerSeeding.CreatePlayerAsync(db, "BugUpgrade", DateTimeOffset.UtcNow);
        var before = await Tables(db);
        await db.Database.MigrateAsync();
        Assert.Equal(["bug_reports"], (await Tables(db)).Except(before));
        Assert.True(await db.PlayerProfiles.AnyAsync(row => row.Id == player.Value));
        Assert.Empty(await db.BugReports.ToListAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static Task<List<string>> Tables(Level5V2DbContext db) => db.Database.SqlQueryRaw<string>(
        "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'").ToListAsync();
}
