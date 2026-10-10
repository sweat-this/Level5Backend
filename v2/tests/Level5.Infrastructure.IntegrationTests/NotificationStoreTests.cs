using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Domain.Platform;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class NotificationStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Existence_uses_the_entire_durable_unique_tuple_and_ignores_read_state()
    {
        await using var db = fixture.CreateDbContext();
        var recipient = await PlayerSeeding.CreatePlayerAsync(db, "exists", Now);
        var item = Create(recipient, "bucket", Now);
        item.SetRead(true, Now);
        var store = new NotificationStore(db);
        await store.AddAsync(item, default);
        Assert.False(await store.ExistsAsync(recipient, item.Source, "bucket", default));
        await db.SaveChangesAsync();
        Assert.True(await store.ExistsAsync(recipient, item.Source, "bucket", default));
        Assert.False(await store.ExistsAsync(PlayerId.New(), item.Source, "bucket", default));
        Assert.False(await store.ExistsAsync(recipient, "other", "bucket", default));
        Assert.False(await store.ExistsAsync(recipient, item.Source, "other", default));
    }

    [Fact]
    public async Task Unique_source_event_and_player_foreign_key_are_enforced_by_Postgres()
    {
        await using var db = fixture.CreateDbContext();
        var recipient = await PlayerSeeding.CreatePlayerAsync(db, "notifyconstraints", Now);
        var store = new NotificationStore(db);
        await store.AddAsync(Create(recipient, "same-event", Now), CancellationToken.None);
        await store.AddAsync(Create(recipient, "same-event", Now.AddMinutes(1)), CancellationToken.None);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        await using var unknownPlayerDb = fixture.CreateDbContext();
        await new NotificationStore(unknownPlayerDb).AddAsync(
            Create(PlayerId.New(), "unknown-player", Now), CancellationToken.None);
        await Assert.ThrowsAsync<DbUpdateException>(() => unknownPlayerDb.SaveChangesAsync());
    }

    [Fact]
    public async Task List_is_recipient_scoped_newest_first_and_keyset_paginated()
    {
        await using var db = fixture.CreateDbContext();
        var recipient = await PlayerSeeding.CreatePlayerAsync(db, "notifylist", Now);
        var other = await PlayerSeeding.CreatePlayerAsync(db, "notifyother", Now);
        var store = new NotificationStore(db);
        var old = Create(recipient, "old", Now);
        var tiedLow = Create(recipient, "tied-low", Now.AddMinutes(1), new NotificationId(Guid.Parse("00000000-0000-0000-0000-000000000001")));
        var tiedHigh = Create(recipient, "tied-high", Now.AddMinutes(1), new NotificationId(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")));
        await store.AddAsync(old, CancellationToken.None);
        await store.AddAsync(tiedLow, CancellationToken.None);
        await store.AddAsync(tiedHigh, CancellationToken.None);
        await store.AddAsync(Create(other, "private", Now.AddMinutes(2)), CancellationToken.None);
        await db.SaveChangesAsync();

        var first = await store.ListAsync(recipient, cursor: null, take: 2, CancellationToken.None);
        var second = await store.ListAsync(
            recipient,
            new NotificationCursorPosition(first[^1].CreatedAt, first[^1].Id),
            take: 2,
            CancellationToken.None);

        Assert.Equal([tiedHigh.Id, tiedLow.Id], first.Select(item => item.Id));
        Assert.Equal(old.Id, Assert.Single(second).Id);
        Assert.DoesNotContain(first.Concat(second), item => item.RecipientPlayerId == other);
    }

    [Fact]
    public async Task Read_state_persists_and_another_recipient_cannot_find_the_notification()
    {
        var notificationId = NotificationId.New();
        PlayerId recipient;
        PlayerId other;
        await using (var seedDb = fixture.CreateDbContext())
        {
            recipient = await PlayerSeeding.CreatePlayerAsync(seedDb, "notifyread", Now);
            other = await PlayerSeeding.CreatePlayerAsync(seedDb, "notifyisolated", Now);
            await new NotificationStore(seedDb).AddAsync(
                Create(recipient, "read", Now, notificationId), CancellationToken.None);
            await seedDb.SaveChangesAsync();
        }

        await using (var updateDb = fixture.CreateDbContext())
        {
            var store = new NotificationStore(updateDb);
            var loaded = await store.FindAsync(recipient, notificationId, CancellationToken.None);
            Assert.NotNull(loaded);
            loaded.SetRead(true, Now.AddMinutes(5));
            await store.StageUpdateAsync(loaded, CancellationToken.None);
            await updateDb.SaveChangesAsync();
            Assert.Null(await store.FindAsync(other, notificationId, CancellationToken.None));
        }

        await using var verifyDb = fixture.CreateDbContext();
        var persisted = await new NotificationStore(verifyDb).FindAsync(
            recipient, notificationId, CancellationToken.None);
        Assert.Equal(Now.AddMinutes(5), persisted!.ReadAt);
    }

    private static PlayerNotification Create(
        PlayerId recipient,
        string sourceEventKey,
        DateTimeOffset createdAt,
        NotificationId? id = null)
    {
        var created = PlayerNotification.Create(
            recipient,
            "platform",
            "friend-request-received",
            "New friend request",
            "You received a new friend request.",
            "/account/friends",
            sourceEventKey,
            createdAt);
        return id is null
            ? created
            : PlayerNotification.Rehydrate(
                id.Value,
                created.RecipientPlayerId,
                created.Source,
                created.Kind,
                created.Title,
                created.Body,
                created.ActionPath,
                created.SourceEventKey,
                created.CreatedAt,
                created.ReadAt);
    }
}
