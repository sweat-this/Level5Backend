using System.Data.Common;
using Level5.Application.Abstractions;
using Level5.Application.BloodMoney;
using Level5.Application.Platform;
using Level5.Infrastructure.BloodMoney;
using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;
using Level5.Infrastructure.Persistence.Rows;
using Level5.Domain.Ids;
using Level5.Domain.BloodMoney;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class BloodMoneyChatNotificationTests(PostgresFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Start = BloodMoneyChatTests.Start;
    private static readonly BloodMoneyChatNotificationPolicy Policy = BloodMoneyChatTests.NotificationPolicy;

    [Theory]
    [InlineData(2)][InlineData(3)][InlineData(4)]
    public async Task New_message_notifies_only_other_canonical_participants_with_the_exact_private_safe_envelope(int count)
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed(count);
        var accepted = await Send(id, players[0], body: "secret message; stake 10; balance 100; classic winner");
        var rows = await Notifications(id);
        Assert.Equal(players.Skip(1).Select(p => p.Value).Order(), rows.Select(row => row.RecipientPlayerId).Order());
        Assert.All(rows, row =>
        {
            Assert.Equal("blood-money", row.Source);
            Assert.Equal("challenge-chat", row.Kind);
            Assert.Equal("New challenge chat activity", row.Title);
            Assert.Null(row.Body);
            Assert.Equal("/account/games/blood-money", row.ActionPath);
            Assert.Equal(Policy.SourceEventKey(id, accepted.Message.CreatedAt), row.SourceEventKey);
            Assert.Null(row.ReadAt);
        });
        await using var db = fixture.CreateDbContext();
        Assert.Empty(await db.Set<BloodMoneyChatParticipantStateRow>().Where(row => row.ChallengeId == id.Value).ToListAsync());
    }

    [Theory]
    [InlineData(2)][InlineData(3)][InlineData(4)]
    public async Task Concurrent_senders_and_device_retries_coalesce_without_losing_messages_and_next_bucket_creates_new_items(int count)
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed(count);
        var requests = players.SelectMany(player => Enumerable.Range(0, 3).Select(_ => (Player: player, Key: Guid.NewGuid()))).ToArray();
        var results = await Task.WhenAll(requests.Select(request => Send(id, request.Player, request.Key)));
        Assert.All(results, result => Assert.True(result.Created));
        Assert.Equal(Enumerable.Range(1, count * 3).Select(i => (long)i), results.Select(r => r.Message.Sequence).Order());
        var retries = await Task.WhenAll(requests.Select(request => Send(id, request.Player, request.Key)));
        Assert.All(retries, result => Assert.False(result.Created));
        var first = await Notifications(id);
        Assert.Equal(players.Select(p => p.Value).Order(), first.Select(row => row.RecipientPlayerId).Order());
        await Send(id, players[0], now: Start.AddMinutes(5).AddTicks(-10));
        Assert.Equal(count, (await Notifications(id)).Count);
        await Send(id, players[0], now: Start.AddMinutes(5));
        var next = await Notifications(id);
        Assert.Equal(count * 2 - 1, next.Count);
        Assert.Equal(next.Count, next.Select(row => (row.RecipientPlayerId, row.SourceEventKey)).Distinct().Count());
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Unmute_never_replays_and_a_fresh_message_only_fills_an_absent_bucket(bool notificationExisted)
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed(3);
        if (notificationExisted) await Send(id, players[0]);
        await Mute(id, players[1], true);
        await Send(id, players[0]);
        Assert.Equal(notificationExisted ? 2 : 1, (await Notifications(id)).Count);
        await Mute(id, players[1], false);
        Assert.Equal(notificationExisted ? 2 : 1, (await Notifications(id)).Count);
        await Send(id, players[0]);
        Assert.Equal(2, (await Notifications(id)).Count);
        await Mute(id, players[1], true);
        Assert.Equal(2, (await Notifications(id)).Count);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Mute_and_send_obey_the_same_challenge_lock_in_both_orders(bool muteFirst)
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed(3);
        var gate = new CommitGate();
        await using var firstDb = fixture.CreateDbContext(gate);
        var store = BloodMoneyChatTests.Store(firstDb);
        Task first = muteFirst ? store.SetNotificationsMutedAsync(id, players[1], true, Ct)
            : store.SendAsync(new(id, players[0], Guid.NewGuid(), "hello"), BloodMoneyChatTests.Rate, Ct);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Task second = muteFirst ? Send(id, players[0]) : Mute(id, players[1], true);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Empty(await Notifications(id));
        gate.Release.TrySetResult();
        await Task.WhenAll(first, second);
        var rows = await Notifications(id);
        Assert.Single(rows, row => row.RecipientPlayerId == players[2].Value);
        Assert.Equal(muteFirst ? 0 : 1, rows.Count(row => row.RecipientPlayerId == players[1].Value));
    }

    [Fact]
    public async Task Inbox_read_unread_and_chat_read_state_are_independent_and_never_reset_bucket_identity()
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed();
        await Send(id, players[0]);
        var notification = Assert.Single(await Notifications(id));
        await using (var db = fixture.CreateDbContext())
        {
            var store = new NotificationStore(db);
            var read = new SetNotificationReadStateUseCase(store, new EfUnitOfWork(db), new BloodMoneyChatTests.Clock(Start));
            await read.ExecuteAsync(new(players[1], new(notification.Id), true), Ct);
        }
        await using (var db = fixture.CreateDbContext())
        {
            var chat = BloodMoneyChatTests.Store(db);
            Assert.Equal(0, (await chat.GetParticipantStateAsync(id, players[1], Ct)).LastReadSequence);
            await chat.ListAsync(new(id, players[1], null, null), Ct);
            await chat.AdvanceReadAsync(id, players[1], 1, Ct);
        }
        Assert.Equal(Start, Assert.Single(await Notifications(id)).ReadAt);
        await Send(id, players[0]);
        Assert.Single(await Notifications(id));
        await using (var db = fixture.CreateDbContext())
        {
            await new SetNotificationReadStateUseCase(new NotificationStore(db), new EfUnitOfWork(db), new BloodMoneyChatTests.Clock(Start))
                .ExecuteAsync(new(players[1], new(notification.Id), false), Ct);
            Assert.Equal(1, (await BloodMoneyChatTests.Store(db).GetParticipantStateAsync(id, players[1], Ct)).LastReadSequence);
        }
        await Send(id, players[0]);
        Assert.Null(Assert.Single(await Notifications(id)).ReadAt);
        await Send(id, players[0], now: Start.AddMinutes(5));
        Assert.Equal(2, (await Notifications(id)).Count);
    }

    [Fact]
    public async Task Current_inactive_account_is_skipped_even_if_scope_previously_tracked_active_authority()
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed(3);
        await using var db = fixture.CreateDbContext();
        var profile = await db.PlayerProfiles.SingleAsync(row => row.Id == players[1].Value);
        var tracked = await db.Accounts.SingleAsync(row => row.Id == profile.AccountId);
        Assert.Equal("Active", tracked.Status);
        await using (var update = fixture.CreateDbContext())
            await update.Accounts.Where(row => row.Id == profile.AccountId).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, "Disabled"));
        var before = await db.Set<BloodMoneyChallengeRow>().AsNoTracking().SingleAsync(row => row.Id == id.Value);
        Assert.True((await BloodMoneyChatTests.Store(db).SendAsync(new(id, players[0], Guid.NewGuid(), "hello"), BloodMoneyChatTests.Rate, Ct)).Created);
        Assert.Equal(players[2].Value, Assert.Single(await Notifications(id)).RecipientPlayerId);
        var after = await db.Set<BloodMoneyChallengeRow>().AsNoTracking().SingleAsync(row => row.Id == id.Value);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Status, after.Status);
    }

    [Fact]
    public async Task Partial_notification_staging_failure_rolls_back_everything_and_same_scope_retries_cleanly()
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed(4);
        await using var db = fixture.CreateDbContext();
        var clock = new BloodMoneyChatTests.Clock(Start);
        var writer = new FailAfterStagingWriter(new NotificationWriter(new NotificationStore(db), clock));
        var store = new BloodMoneyChatStore(db, clock, BloodMoneyChatTests.Cursors, writer,
            new PlayerProfileStore(db), new AccountStore(db), Policy);
        var request = new SendBloodMoneyChallengeMessageRequest(id, players[0], Guid.NewGuid(), "hello");
        await Assert.ThrowsAsync<ForcedNotificationFailure>(() => store.SendAsync(request, BloodMoneyChatTests.Rate, Ct));
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Empty(await Notifications(id));
        await using (var check = fixture.CreateDbContext())
        {
            Assert.False(await check.Set<BloodMoneyChatMessageRow>().AnyAsync(row => row.ChallengeId == id.Value));
            Assert.False(await check.Set<BloodMoneyChatParticipantStateRow>().AnyAsync(row => row.ChallengeId == id.Value));
        }
        Assert.True((await store.SendAsync(request, BloodMoneyChatTests.Rate, Ct)).Created);
        Assert.Equal(3, (await Notifications(id)).Count);
    }

    private async Task<SendBloodMoneyChallengeMessageResult> Send(BloodMoneyChallengeId id, PlayerId actor, Guid? key = null,
        DateTimeOffset? now = null, string body = "hello")
    {
        await using var db = fixture.CreateDbContext();
        return await BloodMoneyChatTests.Store(db, new BloodMoneyChatTests.Clock(now ?? Start))
            .SendAsync(new(id, actor, key ?? Guid.NewGuid(), body), BloodMoneyChatTests.Rate, Ct);
    }

    private async Task Mute(BloodMoneyChallengeId id, PlayerId actor, bool muted)
    {
        await using var db = fixture.CreateDbContext();
        await BloodMoneyChatTests.Store(db).SetNotificationsMutedAsync(id, actor, muted, Ct);
    }

    private async Task<List<PlayerNotificationRow>> Notifications(BloodMoneyChallengeId id)
    {
        await using var db = fixture.CreateDbContext();
        var prefix = $"chat-v1:{id.Value:N}:";
        return await db.PlayerNotifications.AsNoTracking().Where(row => row.Source == "blood-money" && row.SourceEventKey.StartsWith(prefix)).ToListAsync();
    }

    private sealed class CommitGate : DbTransactionInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return result;
        }
    }

    private sealed class ForcedNotificationFailure : Exception;
    private sealed class FailAfterStagingWriter(INotificationWriter inner) : INotificationWriter
    {
        private int _calls;
        public Task<bool> ExistsAsync(PlayerId recipient, string source, string key, CancellationToken token)
            => inner.ExistsAsync(recipient, source, key, token);
        public async Task WriteAsync(NotificationDraft draft, CancellationToken token)
        {
            await inner.WriteAsync(draft, token);
            if (++_calls == 2) throw new ForcedNotificationFailure();
        }
    }
}
