using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Platform;
using Level5.Domain.Ids;
using Level5.Domain.Platform;

namespace Level5.Application.Tests.Platform;

public sealed class BugReportUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Server_identity_time_replay_conflict_and_distinct_identical_reports()
    {
        var store = new Store();
        var useCase = new SubmitBugReportUseCase(store, store, new Clock());
        var request = new SubmitBugReportRequest(PlayerId.New(), Guid.NewGuid(), "level5", " title ", "description");
        var first = await useCase.ExecuteAsync(request, default);
        Assert.True(first.Created);
        Assert.Equal(Now, store.Committed!.CreatedAt);
        Assert.Equal(request.Reporter, store.Committed.ReporterPlayerId);
        var replay = await useCase.ExecuteAsync(request with { Title = "title" }, default);
        Assert.False(replay.Created);
        Assert.Equal(first.Id, replay.Id);
        await Assert.ThrowsAsync<ConflictException>(() => useCase.ExecuteAsync(request with { Description = "different" }, default));
        var second = await useCase.ExecuteAsync(request with { ClientSubmissionId = Guid.NewGuid() }, default);
        Assert.True(second.Created);
        Assert.NotEqual(first.Id, second.Id);
        await Assert.ThrowsAsync<ValidationFailedException>(() => useCase.ExecuteAsync(request with { ClientSubmissionId = Guid.Empty }, default));
    }

    [Fact]
    public async Task Failed_commit_does_not_acknowledge_or_leave_a_pending_write()
    {
        var store = new Store { FailCommit = true };
        var useCase = new SubmitBugReportUseCase(store, store, new Clock());
        await Assert.ThrowsAsync<IOException>(() => useCase.ExecuteAsync(
            new(PlayerId.New(), Guid.NewGuid(), "zromp", "title", "description"), default));
        Assert.Null(store.Pending);
        Assert.Null(store.Committed);
    }

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => Now; }
    private sealed class Store : IBugReportStore, IUnitOfWork
    {
        public BugReport? Pending { get; private set; }
        public BugReport? Committed { get; private set; }
        public bool FailCommit { get; init; }
        public Task<BugReport?> FindAsync(PlayerId reporter, Guid id, CancellationToken ct)
            => Task.FromResult(Committed is { } row && row.ReporterPlayerId == reporter && row.ClientSubmissionId == id ? row : null);
        public Task StageAsync(BugReport report, CancellationToken ct) { Pending = report; return Task.CompletedTask; }
        public void Discard(BugReport report) => Pending = null;
        public Task SaveChangesAsync(CancellationToken ct)
        {
            if (FailCommit) throw new IOException("simulated unavailable database");
            Committed = Pending;
            Pending = null;
            return Task.CompletedTask;
        }
    }
}
