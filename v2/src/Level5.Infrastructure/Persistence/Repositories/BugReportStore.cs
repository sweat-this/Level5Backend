using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Domain.Platform;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.Persistence.Repositories;

public sealed class BugReportStore(Level5V2DbContext db) : IBugReportStore
{
    public async Task<BugReport?> FindAsync(PlayerId reporter, Guid clientSubmissionId, CancellationToken cancellationToken)
    {
        var row = await db.BugReports.AsNoTracking().SingleOrDefaultAsync(
            r => r.ReporterPlayerId == reporter.Value && r.ClientSubmissionId == clientSubmissionId, cancellationToken);
        return row is null ? null : new(row.Id, new(row.ReporterPlayerId), row.ClientSubmissionId,
            BugReportContent.Create(row.GameKey, row.Title, row.Description, row.Category, row.StepsToReproduce, row.GameVersion), row.CreatedAt);
    }

    public Task StageAsync(BugReport report, CancellationToken cancellationToken)
        => db.BugReports.AddAsync(new BugReportRow
        {
            Id = report.Id, ReporterPlayerId = report.ReporterPlayerId.Value, ClientSubmissionId = report.ClientSubmissionId,
            GameKey = report.Content.GameKey, Title = report.Content.Title, Description = report.Content.Description,
            Category = report.Content.Category, StepsToReproduce = report.Content.StepsToReproduce,
            GameVersion = report.Content.GameVersion, CreatedAt = report.CreatedAt
        }, cancellationToken).AsTask();

    public void Discard(BugReport report)
    {
        var entry = db.ChangeTracker.Entries<BugReportRow>().SingleOrDefault(e => e.Entity.Id == report.Id);
        if (entry is not null) entry.State = EntityState.Detached;
    }
}
