using Level5.Domain.Ids;
using Level5.Domain.Platform;

namespace Level5.Application.Abstractions;

public interface IBugReportStore
{
    Task<BugReport?> FindAsync(PlayerId reporter, Guid clientSubmissionId, CancellationToken cancellationToken);
    Task StageAsync(BugReport report, CancellationToken cancellationToken);
    void Discard(BugReport report);
}
