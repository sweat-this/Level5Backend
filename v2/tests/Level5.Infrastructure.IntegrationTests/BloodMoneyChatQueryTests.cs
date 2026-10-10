using System.Data.Common;
using Level5.Application.BloodMoney;
using Level5.Application.Common;
using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;
using Level5.Infrastructure.BloodMoney;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class BloodMoneyChatQueryTests(PostgresFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static IBloodMoneyChatCursorCodec Cursors => BloodMoneyChatTests.Cursors;

    [Theory]
    [InlineData("pending")][InlineData("decline")][InlineData("cancel")][InlineData("expire")]
    public async Task Every_never_active_state_hides_listing_and_report_intake(string state)
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed(active: false);
        if (state != "pending")
        {
            await using var db = fixture.CreateDbContext();
            await BloodMoneyChatTests.Transition(db, id, state == "decline" ? players[1] : players[0], state,
                state == "expire" ? BloodMoneyChatTests.Start.AddHours(1) : BloodMoneyChatTests.Start);
        }
        foreach (var actor in players)
        {
            await Assert.ThrowsAsync<NotFoundException>(() => List(id, actor, cursor: "bad"));
            await Assert.ThrowsAsync<NotFoundException>(() => Report(id, actor, Guid.NewGuid(), (BloodMoneyChatReportReason)100));
        }
    }

    [Theory]
    [InlineData(null, 50)][InlineData("1", 1)][InlineData("100", 100)]
    public async Task Initial_page_is_newest_bounded_ascending_and_older_traversal_is_stable_under_inserts(string? limit, int count)
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed();
        await History(id, players[0], Enumerable.Range(1, 110).Select(i => (long)i));
        var first = await List(id, players[0], limit);
        Assert.Equal(Enumerable.Range(111 - count, count).Select(i => (long)i), first.Items.Select(item => item.Sequence));
        Assert.Equal(110, first.LatestSequence); Assert.Equal(0, first.LastReadSequence);
        Assert.False(first.IsReadOnly); Assert.False(first.NotificationsMuted); Assert.NotNull(first.OlderCursor);
        await History(id, players[1], [111, 112]);
        var seen = new List<long>(first.Items.Select(item => item.Sequence));
        var page = first;
        while (page.OlderCursor is not null)
        {
            page = await List(id, players[0], limit, page.OlderCursor);
            Assert.Equal(112, page.LatestSequence);
            Assert.Equal(110, Cursors.Decode(page.ResumeCursor, id).BoundarySequence);
            Assert.Equal(page.Items.Select(item => item.Sequence).Order(), page.Items.Select(item => item.Sequence));
            seen.AddRange(page.Items.Select(item => item.Sequence));
        }
        Assert.Equal(Enumerable.Range(1, 110).Select(i => (long)i), seen.Order());
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task Empty_history_resume_backlog_gaps_and_suppressed_tombstones_never_skip_or_duplicate()
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed();
        var empty = await List(id, players[0]);
        Assert.Empty(empty.Items); Assert.Null(empty.OlderCursor); Assert.Equal(0, empty.LatestSequence);
        Assert.Equal(0, Cursors.Decode(empty.ResumeCursor, id).BoundarySequence);
        await History(id, players[0], [1, 3, 7, 10, 15], suppressed: 7);
        var first = await List(id, players[0], "2", empty.ResumeCursor);
        Assert.Equal(new long[] { 1, 3 }, first.Items.Select(item => item.Sequence));
        Assert.Equal(15, first.LatestSequence); Assert.Equal(3, Cursors.Decode(first.ResumeCursor, id).BoundarySequence);
        var second = await List(id, players[0], "2", first.ResumeCursor);
        Assert.Equal(new long[] { 7, 10 }, second.Items.Select(item => item.Sequence));
        Assert.Null(second.Items[0].Body); Assert.Equal(BloodMoneyChatVisibility.Suppressed, second.Items[0].Visibility);
        var third = await List(id, players[0], "2", second.ResumeCursor);
        Assert.Equal(15, Assert.Single(third.Items).Sequence);
        var caughtUp = await List(id, players[0], "2", third.ResumeCursor);
        Assert.Empty(caughtUp.Items); Assert.Equal(third.ResumeCursor, caughtUp.ResumeCursor);
        // Gaps are valid read positions, and listing exposes only this actor's state.
        await using var db = fixture.CreateDbContext(); var store = Store(db);
        await store.AdvanceReadAsync(id, players[0], 6, Ct); await store.SetNotificationsMutedAsync(id, players[0], true, Ct);
        Assert.Equal(6, (await List(id, players[0])).LastReadSequence);
        Assert.True((await List(id, players[0])).NotificationsMuted);
        Assert.Equal(0, (await List(id, players[1])).LastReadSequence);
        Assert.False((await List(id, players[1])).NotificationsMuted);
    }

    [Theory]
    [InlineData("0")][InlineData("101")][InlineData("bad")][InlineData("-1")][InlineData("")]
    public async Task Limits_are_validated_only_after_resource_authorization(string limit)
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed();
        Assert.Equal("invalid_chat_limit", (await Assert.ThrowsAsync<BloodMoneyChatException>(() => List(id, players[0], limit))).Code);
        await Assert.ThrowsAsync<NotFoundException>(() => List(id, PlayerId.New(), limit));
        await Assert.ThrowsAsync<NotFoundException>(() => List(new(Guid.NewGuid()), players[0], limit));
    }

    [Fact]
    public async Task Invalid_and_cross_challenge_cursors_do_not_grant_authority()
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed();
        var foreign = Cursors.Encode(new(BloodMoneyChatDirection.Resume, new(Guid.NewGuid()), 0, 0));
        foreach (var cursor in new[] { "bad", new string('A', 1025), foreign })
        {
            Assert.Equal("invalid_chat_cursor", (await Assert.ThrowsAsync<BloodMoneyChatException>(() => List(id, players[0], cursor: cursor))).Code);
            await Assert.ThrowsAsync<NotFoundException>(() => List(id, PlayerId.New(), cursor: cursor));
        }
        var pending = await new BloodMoneyChatTests(fixture).Seed(active: false);
        await Assert.ThrowsAsync<NotFoundException>(() => List(pending.Id, pending.Players[0], cursor: "bad"));
    }

    [Fact]
    public async Task Listing_has_one_committed_view_while_send_and_state_writes_finish_without_waiting_for_it()
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed();
        await History(id, players[0], [1]);
        var gate = new SnapshotGate(); await using var reading = fixture.CreateDbContext(gate);
        var pending = Store(reading).ListAsync(new(id, players[0]), Ct);
        await gate.Captured.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await using var writer = fixture.CreateDbContext();
            var store = Store(writer);
            await store.SendAsync(new(id, players[1], Guid.NewGuid(), "new commit"), BloodMoneyChatTests.Rate, Ct).WaitAsync(TimeSpan.FromSeconds(5));
            await store.AdvanceReadAsync(id, players[0], 2, Ct).WaitAsync(TimeSpan.FromSeconds(5));
            await store.SetNotificationsMutedAsync(id, players[0], true, Ct).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { gate.Release.TrySetResult(); }
        var snapshot = await pending;
        Assert.Equal(1, snapshot.LatestSequence); Assert.Single(snapshot.Items);
        Assert.Equal(0, snapshot.LastReadSequence); Assert.False(snapshot.NotificationsMuted);
        var current = await List(id, players[0]);
        Assert.Equal(2, current.LatestSequence); Assert.Equal(2, current.LastReadSequence); Assert.True(current.NotificationsMuted);
    }

    [Fact]
    public async Task Concurrent_reports_are_duplicate_safe_immutable_and_retain_original_evidence_through_suppression()
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed();
        await History(id, players[0], [1]);
        await using var check = fixture.CreateDbContext();
        var message = await check.Set<BloodMoneyChatMessageRow>().AsNoTracking().SingleAsync(row => row.ChallengeId == id.Value);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try { await Report(id, players[0], message.Id); return "accepted"; }
            catch (BloodMoneyChatException e) { return e.Code; }
        }));
        Assert.Single(attempts, outcome => outcome == "accepted");
        Assert.Equal(7, attempts.Count(outcome => outcome == "chat_report_already_exists"));
        Assert.Equal("chat_report_already_exists", (await Assert.ThrowsAsync<BloodMoneyChatException>(() =>
            Report(id, players[0], message.Id, BloodMoneyChatReportReason.Other))).Code);
        await check.Database.ExecuteSqlInterpolatedAsync($"""UPDATE blood_money_chat_messages SET "Visibility" = 'Suppressed' WHERE "Id" = {message.Id}""");
        await Report(id, players[1], message.Id, BloodMoneyChatReportReason.Spam);
        var reports = await check.Set<BloodMoneyChatReportRow>().AsNoTracking().Where(row => row.ChallengeId == id.Value).ToListAsync();
        Assert.Equal(2, reports.Count); Assert.All(reports, row => Assert.Equal(BloodMoneyChatTests.Start, row.ReportedAt));
        Assert.Equal("Harassment", reports.Single(row => row.ReporterPlayerId == players[0].Value).Reason);
        Assert.Equal(message.Body, (await check.Set<BloodMoneyChatMessageRow>().AsNoTracking().SingleAsync(row => row.Id == message.Id)).Body);
        Assert.Null(Assert.Single((await List(id, players[0])).Items).Body);
        Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() =>
            check.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM blood_money_chat_messages WHERE "Id" = {message.Id}"""))).SqlState);
    }

    [Fact]
    public async Task Report_authorizes_before_reason_or_target_and_scopes_target_to_challenge()
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed();
        var foreign = await new BloodMoneyChatTests(fixture).Seed();
        await History(foreign.Id, foreign.Players[0], [1]);
        await using var db = fixture.CreateDbContext();
        var message = await db.Set<BloodMoneyChatMessageRow>().SingleAsync(row => row.ChallengeId == foreign.Id.Value);
        await Assert.ThrowsAsync<NotFoundException>(() => Report(id, players[0], message.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Report(id, players[0], Guid.NewGuid()));
        await Assert.ThrowsAsync<NotFoundException>(() => Report(id, PlayerId.New(), message.Id, (BloodMoneyChatReportReason)100));
        Assert.Equal("invalid_chat_report", (await Assert.ThrowsAsync<BloodMoneyChatException>(() =>
            Report(id, players[0], message.Id, (BloodMoneyChatReportReason)100))).Code);
    }

    [Theory]
    [InlineData("reason")][InlineData("identity")][InlineData("target")][InlineData("reporter")][InlineData("duplicate")]
    public async Task PostgreSQL_defends_report_reason_identity_target_roster_and_uniqueness(string invalid)
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed(); await History(id, players[0], [1]);
        var foreign = await new BloodMoneyChatTests(fixture).Seed(); await History(foreign.Id, foreign.Players[0], [1]);
        await using var db = fixture.CreateDbContext();
        var messageId = await db.Set<BloodMoneyChatMessageRow>().Where(row => row.ChallengeId == id.Value).Select(row => row.Id).SingleAsync();
        var row = new BloodMoneyChatReportRow { ReportId = Guid.NewGuid(), ChallengeId = id.Value, MessageId = messageId,
            ReporterPlayerId = players[0].Value, Reason = "Hate", ReportedAt = BloodMoneyChatTests.Start };
        if (invalid == "reason") row.Reason = "Unknown";
        if (invalid == "identity") row.ReportId = Guid.Empty;
        if (invalid == "target") row.MessageId = await db.Set<BloodMoneyChatMessageRow>().Where(r => r.ChallengeId == foreign.Id.Value).Select(r => r.Id).SingleAsync();
        if (invalid == "reporter") row.ReporterPlayerId = foreign.Players[0].Value;
        if (invalid == "duplicate") { await Report(id, players[0], messageId); }
        db.Add(row);
        var error = Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException);
        Assert.Equal(invalid is "reason" or "identity" ? PostgresErrorCodes.CheckViolation : invalid == "duplicate"
            ? PostgresErrorCodes.UniqueViolation : PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    private BloodMoneyChatStore Store(Level5.Infrastructure.Persistence.Level5V2DbContext db)
        => BloodMoneyChatTests.Store(db);
    private async Task<BloodMoneyChatPage> List(BloodMoneyChallengeId id, PlayerId actor, string? limit = null, string? cursor = null)
    { await using var db = fixture.CreateDbContext(); return await Store(db).ListAsync(new(id, actor, limit, cursor), Ct); }
    private async Task<BloodMoneyChatReportAcceptance> Report(BloodMoneyChallengeId id, PlayerId actor, Guid messageId,
        BloodMoneyChatReportReason reason = BloodMoneyChatReportReason.Harassment)
    { await using var db = fixture.CreateDbContext(); return await Store(db).ReportAsync(new(id, actor, messageId, reason), Ct); }
    private async Task History(BloodMoneyChallengeId id, PlayerId actor, IEnumerable<long> sequences, long? suppressed = null)
    {
        await using var db = fixture.CreateDbContext();
        db.AddRange(sequences.Select(sequence => new BloodMoneyChatMessageRow { Id = Guid.NewGuid(), ChallengeId = id.Value,
            SenderPlayerId = actor.Value, ClientMessageId = Guid.NewGuid(), Sequence = sequence, Body = $"protected-{sequence}",
            CreatedAt = BloodMoneyChatTests.Start, Visibility = sequence == suppressed ? "Suppressed" : "Visible" }));
        await db.SaveChangesAsync();
    }

    private sealed class SnapshotGate : DbCommandInterceptor
    {
        public TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("max(", StringComparison.OrdinalIgnoreCase))
            { Captured.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
            return result;
        }
    }
}
