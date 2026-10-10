using System.Data.Common;
using Level5.Application.Abstractions;
using Level5.Application.BloodMoney;
using Level5.Application.Common;
using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;
using Level5.Domain.Social;
using Level5.Infrastructure.BloodMoney;
using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Level5.Infrastructure.DependencyInjection;
using Npgsql;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class BloodMoneyChatTests(PostgresFixture fixture)
{
    internal static readonly IBloodMoneyChatCursorCodec Cursors = new HmacBloodMoneyChatCursorCodec("test-only-stable-cursor-secret-32-bytes-minimum");
    private static readonly CancellationToken Ct = CancellationToken.None;
    internal static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    internal static readonly BloodMoneyChatRatePolicy Rate = new(5, TimeSpan.FromSeconds(10), 30, TimeSpan.FromMinutes(1));
    private static readonly BloodMoneyChallengeTimingPolicy Timing = new(TimeSpan.FromHours(1), TimeSpan.FromHours(2));

    [Theory]
    [InlineData(2)][InlineData(3)][InlineData(4)]
    public async Task Participant_sends_are_ordered_attributed_server_timed_durable_and_financially_isolated(int count)
    {
        var (id, players) = await Seed(count);
        var before = await AuthoritySnapshot();
        var results = new List<SendBloodMoneyChallengeMessageResult>();
        foreach (var actor in players)
        {
            var message = await Send(id, actor, body: " e\u0301\r\nhello ");
            results.Add(message);
            Assert.True(message.Created); Assert.NotEqual(Guid.Empty, message.Message.MessageId);
            Assert.Equal(actor, message.Message.SenderPlayerId); Assert.Equal(Start, message.Message.CreatedAt);
            Assert.Equal("é\nhello", message.Message.Body); Assert.Equal(BloodMoneyChatVisibility.Visible, message.Message.Visibility);
        }
        Assert.Equal(Enumerable.Range(1, count).Select(i => (long)i), results.Select(r => r.Message.Sequence));
        // Discard every send context, then reconstruct every domain field from PostgreSQL.
        await using var fresh = fixture.CreateDbContext();
        var rows = await fresh.Set<BloodMoneyChatMessageRow>().Where(r => r.ChallengeId == id.Value).OrderBy(r => r.Sequence).ToListAsync();
        Assert.Equal(results.Select(r => r.Message), rows.Select(r => BloodMoneyChatMessageView.From(new(r.Id, new(r.ChallengeId), r.Sequence,
            new(r.SenderPlayerId), r.ClientMessageId, r.Body, r.CreatedAt, Enum.Parse<BloodMoneyChatVisibility>(r.Visibility)))));
        Assert.Empty(await fresh.Set<BloodMoneyChatParticipantStateRow>().Where(r => r.ChallengeId == id.Value).ToListAsync());
        Assert.Equal(before, await AuthoritySnapshot());
    }

    [Theory]
    [InlineData("pending")][InlineData("decline")][InlineData("cancel")][InlineData("expire")]
    public async Task Never_active_states_have_no_chat_and_all_operations_are_enumeration_safe(string status)
    {
        var (id, players) = await Seed(2, false);
        if (status != "pending")
        {
            await using var db = fixture.CreateDbContext();
            await Transition(db, id, status == "decline" ? players[1] : players[0], status,
                status == "expire" ? Start.AddHours(1) : Start);
        }
        var before = await AuthoritySnapshot();
        await Assert.ThrowsAsync<NotFoundException>(() => Send(id, players[0]));
        await using var check = fixture.CreateDbContext(); var store = BloodMoneyChatTests.Store(check, new Clock(Start));
        await Assert.ThrowsAsync<NotFoundException>(() => store.GetParticipantStateAsync(id, players[0], Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => store.AdvanceReadAsync(id, players[0], 0, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => store.SetNotificationsMutedAsync(id, players[0], true, Ct));
        Assert.Empty(await check.Set<BloodMoneyChatMessageRow>().Where(r => r.ChallengeId == id.Value).ToListAsync());
        Assert.Empty(await check.Set<BloodMoneyChatParticipantStateRow>().Where(r => r.ChallengeId == id.Value).ToListAsync());
        Assert.Equal(before, await AuthoritySnapshot());
    }

    [Fact]
    public async Task Unknown_and_nonparticipant_have_the_same_safe_failure_even_with_an_existing_retry_key()
    {
        var (id, players) = await Seed(); var key = Guid.NewGuid();
        await Send(id, players[0], key);
        foreach (var pair in new[] { (new BloodMoneyChallengeId(Guid.NewGuid()), players[0]), (id, PlayerId.New()) })
        {
            var error = await Assert.ThrowsAsync<NotFoundException>(() => Send(pair.Item1, pair.Item2, key));
            Assert.Equal("Challenge chat not found.", error.Message);
            await using var db = fixture.CreateDbContext(); var store = BloodMoneyChatTests.Store(db, new Clock(Start));
            await Assert.ThrowsAsync<NotFoundException>(() => store.GetParticipantStateAsync(pair.Item1, pair.Item2, Ct));
            await Assert.ThrowsAsync<NotFoundException>(() => store.SetNotificationsMutedAsync(pair.Item1, pair.Item2, true, Ct));
            await Assert.ThrowsAsync<NotFoundException>(() => store.AdvanceReadAsync(pair.Item1, pair.Item2, 0, Ct));
        }
        await using var check = fixture.CreateDbContext();
        Assert.Single(await check.Set<BloodMoneyChatMessageRow>().Where(r => r.ChallengeId == id.Value).ToListAsync());
        Assert.Single(await check.PlayerNotifications.Where(r => r.SourceEventKey == NotificationPolicy.SourceEventKey(id, Start)).ToListAsync());
    }

    [Fact]
    public async Task Normalized_replay_is_original_changed_intent_conflicts_and_keys_are_sender_scoped()
    {
        var (id, players) = await Seed(); var key = Guid.NewGuid();
        var first = await Send(id, players[0], key, " e\u0301\r\nx ");
        var replay = await Send(id, players[0], key, "é\nx", Start.AddDays(1));
        Assert.False(replay.Created); Assert.Equal(first.Message, replay.Message);
        Assert.Equal("chat_message_conflict", (await Assert.ThrowsAsync<BloodMoneyChatException>(() => Send(id, players[0], key, "changed"))).Code);
        var other = await Send(id, players[1], key, "different sender");
        Assert.True(other.Created); Assert.Equal(2, other.Message.Sequence);
        await using var check = fixture.CreateDbContext();
        Assert.Equal(2, await check.Set<BloodMoneyChatMessageRow>().CountAsync(r => r.ChallengeId == id.Value));
        Assert.Equal("é\nx", (await check.Set<BloodMoneyChatMessageRow>().SingleAsync(r => r.Id == first.Message.MessageId)).Body);
    }

    [Fact]
    public async Task Suppressed_retry_returns_tombstone_and_never_exposes_original_body()
    {
        var (id, players) = await Seed(); var key = Guid.NewGuid();
        var accepted = await Send(id, players[0], key, "protected evidence");
        await using (var db = fixture.CreateDbContext())
            // Test fixture only: #90 owns the future authorized suppression operation.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE blood_money_chat_messages SET "Visibility" = 'Suppressed' WHERE "Id" = {accepted.Message.MessageId}
                """);
        var replay = await Send(id, players[0], key, "protected evidence");
        Assert.False(replay.Created); Assert.Null(replay.Message.Body);
        Assert.Equal(accepted.Message.MessageId, replay.Message.MessageId); Assert.Equal(accepted.Message.Sequence, replay.Message.Sequence);
        Assert.Equal(BloodMoneyChatVisibility.Suppressed, replay.Message.Visibility);
    }

    [Fact]
    public async Task Gameplay_deadline_and_later_friendship_removal_do_not_override_canonical_active_chat()
    {
        var (id, players) = await Seed();
        await using (var db = fixture.CreateDbContext())
        {
            var friends = new FriendshipStore(db);
            await friends.RemoveFriendshipAsync((await friends.FindFriendshipBetweenAsync(players[0], players[1], Ct))!, Ct);
            await db.SaveChangesAsync();
        }
        var now = Start.AddDays(1).AddTicks(9);
        var accepted = await Send(id, players[1], now: now);
        Assert.True(accepted.Created); Assert.Equal(Start.AddDays(1), accepted.Message.CreatedAt);
        var retry = await Send(id, players[1], accepted.Message.ClientMessageId, now: now.AddDays(1));
        Assert.Equal(accepted.Message, retry.Message);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Concurrent_same_key_commits_once_and_loser_replays_or_conflicts(bool changed)
    {
        var (id, players) = await Seed(); var key = Guid.NewGuid(); var gate = new LockGate();
        await using var firstDb = fixture.CreateDbContext(gate);
        var firstTask = SendUsing(firstDb, id, players[0], key, "first", Start);
        await gate.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var secondTask = Observe(() => Send(id, players[0], key, changed ? "different" : "first"));
        gate.Release.TrySetResult(); var first = await firstTask; var second = await secondTask;
        if (changed) Assert.Equal("chat_message_conflict", Assert.IsType<BloodMoneyChatException>(second.Error).Code);
        else { Assert.Null(second.Error); Assert.False(second.Result!.Created); Assert.Equal(first.Message, second.Result.Message); }
        await using var check = fixture.CreateDbContext();
        Assert.Single(await check.Set<BloodMoneyChatMessageRow>().Where(r => r.ChallengeId == id.Value).ToListAsync());
    }

    [Theory]
    [InlineData(2)][InlineData(3)][InlineData(4)]
    public async Task Concurrent_participants_preserve_every_accepted_write_and_unique_commit_order(int count)
    {
        var (id, players) = await Seed(count); var before = await AuthoritySnapshot();
        var results = await Task.WhenAll(players.SelectMany(actor => Enumerable.Range(0, 5).Select(_ => Send(id, actor))));
        Assert.All(results, r => Assert.True(r.Created));
        Assert.Equal(Enumerable.Range(1, count * 5).Select(i => (long)i), results.Select(r => r.Message.Sequence).Order());
        await using var check = fixture.CreateDbContext();
        var rows = await check.Set<BloodMoneyChatMessageRow>().Where(r => r.ChallengeId == id.Value).ToListAsync();
        Assert.Equal(results.Select(r => r.Message.MessageId).Order(), rows.Select(r => r.Id).Order());
        Assert.Equal(before, await AuthoritySnapshot());
    }

    [Fact]
    public async Task Persistent_short_window_cannot_be_exceeded_retries_do_not_count_and_sender_budgets_are_independent()
    {
        var (id, players) = await Seed(); var before = await AuthoritySnapshot();
        var attempts = await Task.WhenAll(Enumerable.Range(0, 14).Select(_ => Observe(() => Send(id, players[0]))));
        var accepted = attempts.Where(a => a.Error is null).Select(a => a.Result!).ToArray();
        Assert.Equal(5, accepted.Length);
        foreach (var rejected in attempts.Where(a => a.Error is not null))
        {
            var error = Assert.IsType<BloodMoneyChatException>(rejected.Error);
            Assert.Equal("chat_rate_limited", error.Code); Assert.Equal(TimeSpan.FromSeconds(10), error.RetryAfter);
        }
        // Every call creates a new context/store, so persistence rather than service lifetime owns accounting.
        foreach (var original in accepted)
        {
            var replay = await Send(id, players[0], original.Message.ClientMessageId);
            Assert.False(replay.Created); Assert.Equal(original.Message, replay.Message);
        }
        Assert.Equal("chat_message_conflict", (await Assert.ThrowsAsync<BloodMoneyChatException>(() =>
            Send(id, players[0], accepted[0].Message.ClientMessageId, "changed"))).Code);
        Assert.True((await Send(id, players[1])).Created);
        var almost = await Assert.ThrowsAsync<BloodMoneyChatException>(() => Send(id, players[0], now: Start.AddSeconds(9)));
        Assert.Equal(TimeSpan.FromSeconds(1), almost.RetryAfter);
        Assert.True((await Send(id, players[0], now: Start.AddSeconds(10))).Created); // open lower boundary
        Assert.Equal(before, await AuthoritySnapshot());
    }

    [Fact]
    public async Task Persistent_long_window_rejects_concurrent_sends_until_exact_boundary_and_reports_full_retry_delay()
    {
        var (id, players) = await Seed();
        for (var batch = 0; batch < 6; batch++)
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Send(id, players[0], now: Start.AddSeconds(batch * 10))));
        var failures = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Assert.ThrowsAsync<BloodMoneyChatException>(() => Send(id, players[0], now: Start.AddSeconds(59)))));
        Assert.All(failures, e => { Assert.Equal("chat_rate_limited", e.Code); Assert.Equal(TimeSpan.FromSeconds(1), e.RetryAfter); });
        // At 50s both windows are full; the larger delay is required.
        Assert.Equal(TimeSpan.FromSeconds(10), (await Assert.ThrowsAsync<BloodMoneyChatException>(() => Send(id, players[0], now: Start.AddSeconds(50)))).RetryAfter);
        var reopened = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Send(id, players[0], now: Start.AddMinutes(1))));
        Assert.All(reopened, r => Assert.True(r.Created));
        await using var check = fixture.CreateDbContext();
        Assert.Equal(35, await check.Set<BloodMoneyChatMessageRow>().CountAsync(r => r.ChallengeId == id.Value));
    }

    [Theory]
    [InlineData(false, 10)][InlineData(true, 10)]
    [InlineData(false, 2000)][InlineData(true, 2000)]
    public async Task Slower_writer_and_backward_clock_cannot_reset_either_persistent_budget(bool longWindow, int correctionMilliseconds)
    {
        var (id, players) = await Seed(); var before = await AuthoritySnapshot();
        // A nonbinding short limit isolates enforcement of the long window using the same store path.
        var rate = longWindow ? new BloodMoneyChatRatePolicy(100, Rate.ShortWindow, Rate.LongLimit, Rate.LongWindow) : Rate;
        var limit = longWindow ? rate.LongLimit : rate.ShortLimit;
        var window = longWindow ? rate.LongWindow : rate.ShortWindow;
        var firstKey = Guid.NewGuid();
        var first = await Send(id, players[0], firstKey, rate: rate);
        for (var index = 1; index < limit; index++) await Send(id, players[0], rate: rate);

        var correction = TimeSpan.FromMilliseconds(correctionMilliseconds);
        // Every contender uses a fresh context, modeling independent writers/restart with a slower clock.
        var rejected = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Assert.ThrowsAsync<BloodMoneyChatException>(() => Send(id, players[0], now: Start - correction, rate: rate))));
        Assert.All(rejected, error =>
        {
            Assert.Equal("chat_rate_limited", error.Code);
            Assert.Equal(window + correction, error.RetryAfter);
        });
        var replay = await Send(id, players[0], firstKey, now: Start - correction, rate: rate);
        Assert.False(replay.Created); Assert.Equal(first.Message, replay.Message);
        await using (var check = fixture.CreateDbContext())
            Assert.Equal(limit, await check.Set<BloodMoneyChatMessageRow>().CountAsync(row => row.ChallengeId == id.Value));
        // Once server time reaches the actual expiry, the open lower boundary permits a new send.
        Assert.Equal(limit + 1, (await Send(id, players[0], now: Start + window, rate: rate)).Message.Sequence);
        Assert.Equal(before, await AuthoritySnapshot());
    }

    [Fact]
    public async Task Cancellation_interrupts_transient_retry_backoff_without_another_attempt_or_partial_state()
    {
        var (id, players) = await Seed(); var before = await AuthoritySnapshot();
        using var cancellation = new CancellationTokenSource();
        await using var configuration = fixture.CreateDbContext();
        var failure = new TransientLockFailure();
        var options = new DbContextOptionsBuilder<Level5V2DbContext>()
            .UseNpgsql(configuration.Database.GetDbConnection().ConnectionString,
                npgsql => npgsql.ExecutionStrategy(dependencies => new CancelledRetryBackoff(dependencies, cancellation)))
            .AddInterceptors(failure).Options;
        await using var db = new Level5V2DbContext(options);
        var send = new SendBloodMoneyChallengeMessageUseCase(BloodMoneyChatTests.Store(db, new Clock(Start)), Rate)
            .ExecuteAsync(new(id, players[0], Guid.NewGuid(), "cancelled"), cancellation.Token);
        try
        {
            // The test strategy selects a ten-second backoff, then cancels. Completion must interrupt it.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { await Record.ExceptionAsync(() => send); }
        Assert.True(cancellation.IsCancellationRequested); Assert.Equal(1, failure.Attempts);
        Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges());
        await using var check = fixture.CreateDbContext();
        Assert.Empty(await check.Set<BloodMoneyChatMessageRow>().Where(row => row.ChallengeId == id.Value).ToListAsync());
        Assert.Empty(await check.Set<BloodMoneyChatParticipantStateRow>().Where(row => row.ChallengeId == id.Value).ToListAsync());
        Assert.Equal(before, await AuthoritySnapshot());
    }

    [Fact]
    public async Task Participant_state_is_private_durable_monotonic_and_read_mute_writes_preserve_each_other()
    {
        var (id, players) = await Seed(); var before = await AuthoritySnapshot();
        for (var i = 0; i < 5; i++) await Send(id, players[0]);
        await using (var db = fixture.CreateDbContext())
        {
            var store = BloodMoneyChatTests.Store(db, new Clock(Start));
            Assert.Equal(new(0, false), await store.GetParticipantStateAsync(id, players[0], Ct));
            Assert.Equal(new(0, true), await store.SetNotificationsMutedAsync(id, players[0], true, Ct));
            Assert.Equal(new(3, true), await store.AdvanceReadAsync(id, players[0], 3, Ct));
            Assert.Equal(new(3, true), await store.AdvanceReadAsync(id, players[0], 1, Ct));
            Assert.Equal(new(3, false), await store.SetNotificationsMutedAsync(id, players[0], false, Ct));
            Assert.Equal(new(0, false), await store.GetParticipantStateAsync(id, players[1], Ct));
            foreach (var invalid in new[] { -1L, 6L })
                Assert.Equal("invalid_chat_read_position", (await Assert.ThrowsAsync<BloodMoneyChatException>(() => store.AdvanceReadAsync(id, players[0], invalid, Ct))).Code);
        }
        await Task.WhenAll(new[] { 5L, 1L, 4L, 2L }.Select(async sequence =>
        {
            await using var db = fixture.CreateDbContext();
            await BloodMoneyChatTests.Store(db, new Clock(Start)).AdvanceReadAsync(id, players[0], sequence, Ct);
        }).Append(Task.Run(async () =>
        {
            await using var db = fixture.CreateDbContext();
            await BloodMoneyChatTests.Store(db, new Clock(Start)).SetNotificationsMutedAsync(id, players[0], true, Ct);
        })));
        await using var fresh = fixture.CreateDbContext();
        var persisted = await BloodMoneyChatTests.Store(fresh, new Clock(Start)).GetParticipantStateAsync(id, players[0], Ct);
        Assert.Equal(new(5, true), persisted);
        Assert.Equal(before, await AuthoritySnapshot());
    }

    [Fact]
    public async Task Concurrent_explicit_mute_sets_serialize_and_last_commit_wins_without_changing_read_position()
    {
        var (id, players) = await Seed(); await Send(id, players[0]);
        await using (var db = fixture.CreateDbContext())
            await BloodMoneyChatTests.Store(db, new Clock(Start)).AdvanceReadAsync(id, players[0], 1, Ct);
        var gate = new CommitGate();
        await using var first = fixture.CreateDbContext(gate);
        var mute = BloodMoneyChatTests.Store(first, new Clock(Start)).SetNotificationsMutedAsync(id, players[0], true, Ct);
        await gate.BeforeCommit.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await using var second = fixture.CreateDbContext();
        var unmute = BloodMoneyChatTests.Store(second, new Clock(Start)).SetNotificationsMutedAsync(id, players[0], false, Ct);
        Assert.False(unmute.IsCompleted); gate.Release.TrySetResult();
        Assert.Equal(new(1, true), await mute); Assert.Equal(new(1, false), await unmute);
        await using var fresh = fixture.CreateDbContext();
        Assert.Equal(new(1, false), await BloodMoneyChatTests.Store(fresh, new Clock(Start)).GetParticipantStateAsync(id, players[0], Ct));
    }

    [Fact]
    public async Task Send_lock_wins_before_activation_and_creates_nothing_until_activation_commits()
    {
        var (id, players) = await Seed(2, false); var gate = new LockGate();
        await using var db = fixture.CreateDbContext(gate);
        var send = Assert.ThrowsAsync<NotFoundException>(() => SendUsing(db, id, players[0], Guid.NewGuid(), "before", Start));
        await gate.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await using var activating = fixture.CreateDbContext();
        var activation = Transition(activating, id, players[1], "accept", Start);
        gate.Release.TrySetResult(); await send; await activation;
        Assert.Equal(1, (await Send(id, players[0])).Message.Sequence);
    }

    [Fact]
    public async Task Activation_lock_wins_and_sender_reloads_even_when_its_context_tracked_pending_state()
    {
        var (id, players) = await Seed(2, false); var gate = new CommitGate();
        await using var sender = fixture.CreateDbContext();
        Assert.Equal(BloodMoneyChallengeStatus.PendingAcceptance, (await new BloodMoneyChallengeStore(sender).FindAsync(id, Ct))!.Status);
        await using var activating = fixture.CreateDbContext(gate);
        var activation = Transition(activating, id, players[1], "accept", Start);
        await gate.BeforeCommit.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var send = SendUsing(sender, id, players[0], Guid.NewGuid(), "after", Start);
        Assert.False(send.IsCompleted);
        gate.Release.TrySetResult(); await activation;
        Assert.True((await send).Created);
    }

    [Fact]
    public async Task Send_holds_challenge_lock_and_does_not_acknowledge_until_commit()
    {
        var (id, players) = await Seed(); var gate = new CommitGate();
        await using var firstDb = fixture.CreateDbContext(gate);
        var first = SendUsing(firstDb, id, players[0], Guid.NewGuid(), "first", Start);
        await gate.BeforeCommit.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var second = Send(id, players[1]);
        Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
        await using (var check = fixture.CreateDbContext())
            Assert.Empty(await check.Set<BloodMoneyChatMessageRow>().Where(r => r.ChallengeId == id.Value).ToListAsync());
        gate.Release.TrySetResult();
        Assert.Equal(1, (await first).Message.Sequence); Assert.Equal(2, (await second).Message.Sequence);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Runtime_execution_strategy_recovers_transient_rollback_or_lost_commit_response_from_durable_state(bool committed)
    {
        var (id, players) = await Seed(); var key = Guid.NewGuid();
        await using var db = fixture.CreateDbContext(new TransientCommitFailure(committed));
        var result = await SendUsing(db, id, players[0], key, "hello", Start);
        Assert.Equal(!committed, result.Created);
        var replay = await Send(id, players[0], key);
        Assert.Equal(result.Message, replay.Message); Assert.False(replay.Created);
        await using var check = fixture.CreateDbContext();
        Assert.Single(await check.Set<BloodMoneyChatMessageRow>().Where(r => r.ChallengeId == id.Value).ToListAsync());
        Assert.Equal(players[1].Value, Assert.Single(await check.PlayerNotifications
            .Where(r => r.SourceEventKey == NotificationPolicy.SourceEventKey(id, Start)).ToListAsync()).RecipientPlayerId);
    }

    [Fact]
    public async Task Failure_before_commit_rolls_back_message_does_not_charge_rate_or_change_state_and_scope_can_retry()
    {
        var (id, players) = await Seed(); var before = await AuthoritySnapshot(); var key = Guid.NewGuid();
        await using (var stateDb = fixture.CreateDbContext())
            await BloodMoneyChatTests.Store(stateDb, new Clock(Start)).SetNotificationsMutedAsync(id, players[0], true, Ct);
        var failure = new FailCommit();
        await using (var db = fixture.CreateDbContext(failure))
        {
            await Assert.ThrowsAsync<ForcedRollbackException>(() => SendUsing(db, id, players[0], key, "hello", Start));
            Assert.False(db.ChangeTracker.HasChanges());
            await using var check = fixture.CreateDbContext();
            Assert.Empty(await check.Set<BloodMoneyChatMessageRow>().Where(r => r.ChallengeId == id.Value).ToListAsync());
            Assert.Empty(await check.PlayerNotifications.Where(r => r.SourceEventKey == NotificationPolicy.SourceEventKey(id, Start)).ToListAsync());
            Assert.Equal(new(0, true), await BloodMoneyChatTests.Store(check, new Clock(Start)).GetParticipantStateAsync(id, players[0], Ct));
            // The interceptor fails only once. A retry must insert, not recover an uncommitted tracked row.
            Assert.True((await SendUsing(db, id, players[0], key, "hello", Start)).Created);
        }
        for (var i = 0; i < 4; i++) await Send(id, players[0]);
        Assert.Equal("chat_rate_limited", (await Assert.ThrowsAsync<BloodMoneyChatException>(() => Send(id, players[0]))).Code);
        Assert.Equal(before, await AuthoritySnapshot());
    }

    [Fact]
    public async Task Failed_state_commit_rolls_back_read_and_mute_and_send_cannot_flush_unrelated_staged_work()
    {
        var (id, players) = await Seed(); await Send(id, players[0]);
        foreach (var read in new[] { true, false })
        {
            await using var db = fixture.CreateDbContext(new FailCommit());
            var store = BloodMoneyChatTests.Store(db, new Clock(Start));
            await Assert.ThrowsAsync<ForcedRollbackException>(() => read
                ? store.AdvanceReadAsync(id, players[0], 1, Ct) : store.SetNotificationsMutedAsync(id, players[0], true, Ct));
        }
        await using var fresh = fixture.CreateDbContext();
        Assert.Equal(new(0, false), await BloodMoneyChatTests.Store(fresh, new Clock(Start)).GetParticipantStateAsync(id, players[0], Ct));
        var challenge = await fresh.Set<BloodMoneyChallengeRow>().SingleAsync(r => r.Id == id.Value);
        challenge.Revision++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => SendUsing(fresh, id, players[0], Guid.NewGuid(), "must not flush", Start));
    }

    [Fact]
    public async Task Opt_in_composition_supplies_rate_and_clock_without_another_context_or_route()
    {
        var (id, players) = await Seed(); var services = new ServiceCollection();
        await using var configurationDb = fixture.CreateDbContext();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:DefaultConnection"] = configurationDb.Database.GetConnectionString() }).Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddLevel5Infrastructure(configuration);
        services.AddSingleton<IClock>(new Clock(Start));
        services.AddSingleton(Cursors);
        services.AddScoped<INotificationWriter, Level5.Application.Platform.NotificationWriter>();
        services.AddBloodMoneyChat(Rate, NotificationPolicy);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Level5V2DbContext>();
        var profiles = scope.ServiceProvider.GetRequiredService<IPlayerProfileStore>();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        var writer = scope.ServiceProvider.GetRequiredService<INotificationWriter>();
        var recipient = await profiles.FindByIdAsync(players[1], Ct);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            // Shared authority queries must see this context's uncommitted changes, not another connection's snapshot.
            await db.PlayerProfiles.Where(row => row.Id == players[1].Value)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.DisplayName, "scoped profile"));
            Assert.Equal("scoped profile", (await profiles.FindByIdAsync(players[1], Ct))!.DisplayName);
            await db.Accounts.Where(row => row.Id == recipient!.AccountId.Value)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, "Disabled"));
            Assert.Equal(Level5.Domain.Identity.AccountStatus.Disabled, (await accounts.FindByIdAsync(recipient!.AccountId, Ct))!.Status);
            var probeKey = Guid.NewGuid().ToString("N");
            await writer.WriteAsync(new(players[1], "platform", "scope-probe", "Scope probe", null, null, probeKey), Ct);
            Assert.Single(db.ChangeTracker.Entries<Level5.Infrastructure.Persistence.Rows.PlayerNotificationRow>());
            await db.SaveChangesAsync();
            Assert.True(await writer.ExistsAsync(players[1], "platform", probeKey, Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<SendBloodMoneyChallengeMessageUseCase>()
                .ExecuteAsync(new(id, players[0], Guid.NewGuid(), "caller transaction"), Ct));
            await transaction.RollbackAsync();
            db.ChangeTracker.Clear();
        });
        Assert.True((await scope.ServiceProvider.GetRequiredService<SendBloodMoneyChallengeMessageUseCase>()
            .ExecuteAsync(new(id, players[0], Guid.NewGuid(), "composed"), Ct)).Created);
        Assert.Single(await db.PlayerNotifications.Where(row => row.SourceEventKey == NotificationPolicy.SourceEventKey(id, Start)).ToListAsync());
    }

    internal async Task<(BloodMoneyChallengeId Id, PlayerId[] Players)> Seed(int count = 2, bool active = true)
    {
        var players = new List<PlayerId>();
        await using var db = fixture.CreateDbContext();
        for (var i = 0; i < count; i++)
        {
            var player = await PlayerSeeding.CreatePlayerAsync(db, "Chat", Start); players.Add(player);
            await new IssueBloodCreditsUseCase(new BloodCreditLedgerStore(db), new EfUnitOfWork(db), new Clock(Start))
                .ExecuteAsync(new(BloodCreditTransactionId.New(), player, 100, "chat-fixture"), Ct);
            if (i > 0) await new FriendshipStore(db).AddFriendshipAsync(Friendship.Between(players[0], player, Start), Ct);
        }
        await db.SaveChangesAsync();
        var financial = new BloodCreditReservationMutator(new BloodCreditLedgerStore(db), new BloodCreditReservationStore(db), new Clock(Start));
        var challenge = await new CreateBloodMoneyChallengeUseCase(new BloodMoneyChallengeStore(db), new FriendshipStore(db), financial,
            new EfUnitOfWork(db), new Clock(Start), Timing)
            .ExecuteAsync(new(players[0], players.Skip(1).ToArray(), 10, "classic", 1, Guid.NewGuid()), Ct);
        if (active)
            foreach (var player in players.Skip(1))
            {
                await using var accepting = fixture.CreateDbContext();
                await Transition(accepting, challenge.ChallengeId, player, "accept", Start);
            }
        return (challenge.ChallengeId, players.ToArray());
    }

    internal static Task<BloodMoneyChallengeView> Transition(Level5V2DbContext db, BloodMoneyChallengeId id, PlayerId actor, string action, DateTimeOffset now)
    {
        var financial = new BloodCreditReservationMutator(new BloodCreditLedgerStore(db), new BloodCreditReservationStore(db), new Clock(now));
        var lifecycle = new BloodMoneyChallengeLifecycle(new BloodMoneyChallengeStore(db), new BloodCreditReservationStore(db), financial, new EfUnitOfWork(db), new Clock(now), Timing);
        return action switch
        {
            "accept" => new AcceptBloodMoneyChallengeUseCase(lifecycle).ExecuteAsync(id, actor, Ct),
            "decline" => new DeclineBloodMoneyChallengeUseCase(lifecycle).ExecuteAsync(id, actor, Ct),
            "cancel" => new CancelBloodMoneyChallengeUseCase(lifecycle).ExecuteAsync(id, actor, Ct),
            "expire" => new ExpirePendingBloodMoneyChallengeUseCase(lifecycle).ExecuteAsync(id, Ct),
            _ => throw new ArgumentException(action)
        };
    }

    private async Task<SendBloodMoneyChallengeMessageResult> Send(BloodMoneyChallengeId id, PlayerId actor, Guid? key = null, string body = "hello", DateTimeOffset? now = null, BloodMoneyChatRatePolicy? rate = null)
    { await using var db = fixture.CreateDbContext(); return await SendUsing(db, id, actor, key ?? Guid.NewGuid(), body, now ?? Start, rate); }
    private static Task<SendBloodMoneyChallengeMessageResult> SendUsing(Level5V2DbContext db, BloodMoneyChallengeId id, PlayerId actor, Guid key, string body, DateTimeOffset now, BloodMoneyChatRatePolicy? rate = null)
        => new SendBloodMoneyChallengeMessageUseCase(BloodMoneyChatTests.Store(db, new Clock(now)), rate ?? Rate).ExecuteAsync(new(id, actor, key, body), Ct);

    private static async Task<(SendBloodMoneyChallengeMessageResult? Result, Exception? Error)> Observe(Func<Task<SendBloodMoneyChallengeMessageResult>> action)
    { try { return (await action(), null); } catch (Exception error) { return (null, error); } }

    private async Task<string[]> AuthoritySnapshot()
    {
        await using var db = fixture.CreateDbContext(); var results = new List<string>();
        foreach (var table in new[] { "blood_money_challenges", "blood_money_challenge_participants", "blood_money_credit_accounts",
            "blood_money_credit_transactions", "blood_money_credit_postings", "blood_money_credit_reservations" })
        {
            // Only fixed test-owned identifiers above are interpolated; values never come from callers.
            var sql = $"SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text AS \"Value\" FROM {table} t";
            results.Add(await db.Database.SqlQueryRaw<string>(sql).SingleAsync());
        }
        return results.ToArray();
    }

    internal static readonly BloodMoneyChatNotificationPolicy NotificationPolicy = new("v1", TimeSpan.FromMinutes(5));
    internal static BloodMoneyChatStore Store(Level5V2DbContext db, IClock? clock = null)
    {
        clock ??= new Clock(Start);
        return new(db, clock, Cursors, new Level5.Application.Platform.NotificationWriter(new NotificationStore(db), clock),
            new PlayerProfileStore(db), new AccountStore(db), NotificationPolicy);
    }

    internal sealed class Clock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }

    private sealed class LockGate : DbCommandInterceptor
    {
        public TaskCompletionSource Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            { Acquired.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
            return result;
        }
    }

    private sealed class CommitGate : DbTransactionInterceptor
    {
        public TaskCompletionSource BeforeCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        { BeforeCommit.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); return result; }
    }

    private sealed class ForcedRollbackException : Exception;
    private sealed class CancelledRetryBackoff(ExecutionStrategyDependencies dependencies, CancellationTokenSource cancellation)
        : ExecutionStrategy(dependencies, 1, TimeSpan.FromSeconds(10))
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is NpgsqlException { IsTransient: true };
        protected override TimeSpan? GetNextDelay(Exception lastException)
        {
            var delay = base.GetNextDelay(lastException);
            if (delay is null) return null;
            cancellation.Cancel();
            return TimeSpan.FromSeconds(10);
        }
    }
    private sealed class TransientLockFailure : DbCommandInterceptor
    {
        public int Attempts { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal) && ++Attempts == 1)
                throw new NpgsqlException("Test transport failure before lock", new IOException("Connection lost"));
            return ValueTask.FromResult(result);
        }
    }
    private sealed class TransientCommitFailure(bool afterCommit) : DbTransactionInterceptor
    {
        private bool _failed;
        private void FailOnce()
        {
            if (!_failed) { _failed = true; throw new NpgsqlException("Test transport failure", new IOException("Connection lost")); }
        }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        { if (!afterCommit) FailOnce(); return ValueTask.FromResult(result); }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        { if (afterCommit) FailOnce(); return Task.CompletedTask; }
    }
    private sealed class FailCommit : DbTransactionInterceptor
    {
        private bool _failed;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!_failed) { _failed = true; throw new ForcedRollbackException(); }
            return ValueTask.FromResult(result);
        }
    }
}
