using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Ids;
using Level5.Domain.Platform;

namespace Level5.Application.Platform;

public sealed record SubmitBugReportRequest(PlayerId Reporter, Guid ClientSubmissionId, string? GameKey,
    string? Title, string? Description, string? Category = null, string? StepsToReproduce = null, string? GameVersion = null);

public sealed record SubmitBugReportResult(Guid Id, bool Created);

public sealed class SubmitBugReportUseCase(IBugReportStore store, IUnitOfWork unitOfWork, IClock clock)
{
    public async Task<SubmitBugReportResult> ExecuteAsync(SubmitBugReportRequest request, CancellationToken cancellationToken)
    {
        if (request.ClientSubmissionId == Guid.Empty)
            throw new ValidationFailedException("Client submission ID must be a nonempty UUID.");
        var content = BugReportContent.Create(request.GameKey, request.Title, request.Description,
            request.Category, request.StepsToReproduce, request.GameVersion);
        var existing = await store.FindAsync(request.Reporter, request.ClientSubmissionId, cancellationToken);
        if (existing is not null) return Replay(existing, content);

        var report = new BugReport(Guid.CreateVersion7(), request.Reporter, request.ClientSubmissionId, content, clock.UtcNow);
        var raced = await IdempotentInsertRecovery.TryInsertAsync(
            async ct =>
            {
                try
                {
                    await store.StageAsync(report, ct);
                    await unitOfWork.SaveChangesAsync(ct);
                }
                catch
                {
                    store.Discard(report);
                    throw;
                }
            },
            ct => store.FindAsync(request.Reporter, request.ClientSubmissionId, ct), cancellationToken);
        return raced is null ? new(report.Id, true) : Replay(raced, content);
    }

    private static SubmitBugReportResult Replay(BugReport report, BugReportContent content)
        => report.Content == content ? new(report.Id, false)
            : throw new ConflictException("This submission ID was already used for a different report.");
}
