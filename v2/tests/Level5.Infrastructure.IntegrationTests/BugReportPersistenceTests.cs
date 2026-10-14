using Level5.Application.Common;
using Level5.Application.Platform;
using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Domain.Platform;
using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class BugReportPersistenceTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static BugReport Report(PlayerId player, Guid key) => new(Guid.CreateVersion7(), player, key,
        BugReportContent.Create("blood-money", "title", "description"), Now);

    [Fact]
    public async Task Durable_replay_uniqueness_foreign_key_and_restrict_delete()
    {
        await using var db = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(db, "bug", Now);
        var key = Guid.NewGuid();
        var store = new BugReportStore(db);
        var report = Report(player, key);
        await store.StageAsync(report, default);
        await new EfUnitOfWork(db).SaveChangesAsync(default);
        Assert.Equal(report, await store.FindAsync(player, key, default));
        Assert.Null(await store.FindAsync(PlayerId.New(), key, default));

        var duplicate = Report(player, key);
        await store.StageAsync(duplicate, default);
        await Assert.ThrowsAsync<ConflictException>(() => new EfUnitOfWork(db).SaveChangesAsync(default));
        store.Discard(duplicate);
        var missingReporter = Report(PlayerId.New(), Guid.NewGuid());
        await store.StageAsync(missingReporter, default);
        var fk = await Assert.ThrowsAsync<DbUpdateException>(() => new EfUnitOfWork(db).SaveChangesAsync(default));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ((PostgresException)fk.InnerException!).SqlState);
        store.Discard(missingReporter);
        var deletion = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM player_profiles WHERE \"Id\" = {player.Value}"));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, deletion.SqlState);
        Assert.Equal(1, await db.BugReports.CountAsync(r => r.ReporterPlayerId == player.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_misses_resolve_using_database_uniqueness(bool conflicting)
    {
        await using var seed = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(seed, "bugrace", Now);
        var request = new SubmitBugReportRequest(player, Guid.NewGuid(), "level5", "title", "description");
        var barrier = new InsertBarrier();
        await using var first = fixture.CreateDbContext(barrier);
        await using var second = fixture.CreateDbContext(barrier);
        async Task<SubmitBugReportResult?> Run(Level5V2DbContext db, SubmitBugReportRequest body)
        {
            try { return await new SubmitBugReportUseCase(new BugReportStore(db), new EfUnitOfWork(db), new Clock()).ExecuteAsync(body, default); }
            catch (ConflictException) when (conflicting) { return null; }
        }
        var results = await Task.WhenAll(Run(first, request), Run(second, request with { Title = conflicting ? "other" : "title" }));
        Assert.Single(results, r => r?.Created == true);
        if (conflicting) Assert.Single(results, r => r is null);
        else Assert.Equal(results[0]!.Id, results[1]!.Id);
        Assert.Equal(1, await seed.BugReports.CountAsync(r => r.ReporterPlayerId == player.Value));
    }

    [Fact]
    public async Task Rolled_back_transaction_leaves_no_report()
    {
        await using var db = fixture.CreateDbContext();
        var player = await PlayerSeeding.CreatePlayerAsync(db, "bugfail", Now);
        var report = Report(player, Guid.NewGuid());
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await new BugReportStore(db).StageAsync(report, default);
            await new EfUnitOfWork(db).SaveChangesAsync(default);
            await transaction.RollbackAsync();
        });
        await using var verify = fixture.CreateDbContext();
        Assert.Null(await new BugReportStore(verify).FindAsync(player, report.ClientSubmissionId, default));
    }

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => Now; }
    private sealed class InsertBarrier : SaveChangesInterceptor
    {
        private int _arrived;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrived) == 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }
    }
}
