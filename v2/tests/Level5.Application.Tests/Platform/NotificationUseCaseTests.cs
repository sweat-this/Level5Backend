using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Platform;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Ids;
using Xunit;

namespace Level5.Application.Tests.Platform;

public sealed class NotificationUseCaseTests
{
    private readonly InMemoryNotificationStore _store = new();
    private readonly FakeClock _clock = new();

    [Fact]
    public async Task Writer_creates_an_unread_notification_without_committing_the_unit_of_work()
    {
        var recipient = PlayerId.New();
        var writer = new NotificationWriter(_store, _clock);

        await writer.WriteAsync(Draft(recipient, "event-1"), CancellationToken.None);

        var notification = Assert.Single(_store.Notifications);
        Assert.Equal(recipient, notification.RecipientPlayerId);
        Assert.Null(notification.ReadAt);
        Assert.Equal(_clock.UtcNow, notification.CreatedAt);
    }

    [Fact]
    public async Task Duplicate_source_event_does_not_create_a_second_notification()
    {
        var recipient = PlayerId.New();
        var writer = new NotificationWriter(_store, _clock);

        await writer.WriteAsync(Draft(recipient, "duplicate"), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAsync(Draft(recipient, "duplicate"), CancellationToken.None));

        Assert.Single(_store.Notifications);
    }

    [Fact]
    public async Task List_is_recipient_scoped_newest_first_and_uses_an_opaque_cursor()
    {
        var recipient = PlayerId.New();
        var other = PlayerId.New();
        var writer = new NotificationWriter(_store, _clock);
        await writer.WriteAsync(Draft(recipient, "old"), CancellationToken.None);
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await writer.WriteAsync(Draft(recipient, "new"), CancellationToken.None);
        await writer.WriteAsync(Draft(other, "private"), CancellationToken.None);
        var useCase = new ListNotificationsUseCase(_store);

        var first = await useCase.ExecuteAsync(new ListNotificationsRequest(recipient, 1, null), CancellationToken.None);
        var second = await useCase.ExecuteAsync(new ListNotificationsRequest(recipient, 1, first.NextCursor), CancellationToken.None);

        Assert.Equal("new", Assert.Single(first.Items).Body);
        Assert.NotNull(first.NextCursor);
        Assert.Equal("old", Assert.Single(second.Items).Body);
        Assert.Null(second.NextCursor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task List_rejects_an_out_of_range_limit(int limit)
    {
        var useCase = new ListNotificationsUseCase(_store);

        await Assert.ThrowsAsync<ValidationFailedException>(() => useCase.ExecuteAsync(
            new ListNotificationsRequest(PlayerId.New(), limit, null), CancellationToken.None));
    }

    [Fact]
    public async Task List_rejects_an_invalid_cursor()
    {
        var useCase = new ListNotificationsUseCase(_store);

        await Assert.ThrowsAsync<ValidationFailedException>(() => useCase.ExecuteAsync(
            new ListNotificationsRequest(PlayerId.New(), 20, "not-a-cursor"), CancellationToken.None));
    }

    [Fact]
    public async Task Read_and_unread_are_idempotent_and_commit_each_requested_state()
    {
        var recipient = PlayerId.New();
        await new NotificationWriter(_store, _clock).WriteAsync(Draft(recipient, "read-state"), CancellationToken.None);
        var notification = Assert.Single(_store.Notifications);
        var unitOfWork = new CountingUnitOfWork();
        var useCase = new SetNotificationReadStateUseCase(_store, unitOfWork, _clock);

        await useCase.ExecuteAsync(new(recipient, notification.Id, true), CancellationToken.None);
        var firstReadAt = notification.ReadAt;
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await useCase.ExecuteAsync(new(recipient, notification.Id, true), CancellationToken.None);
        Assert.Equal(firstReadAt, notification.ReadAt);

        await useCase.ExecuteAsync(new(recipient, notification.Id, false), CancellationToken.None);
        await useCase.ExecuteAsync(new(recipient, notification.Id, false), CancellationToken.None);
        Assert.Null(notification.ReadAt);
        Assert.Equal(4, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task Missing_and_other_players_notification_are_both_not_found()
    {
        var recipient = PlayerId.New();
        await new NotificationWriter(_store, _clock).WriteAsync(Draft(recipient, "private"), CancellationToken.None);
        var notification = Assert.Single(_store.Notifications);
        var useCase = new SetNotificationReadStateUseCase(_store, new NoOpUnitOfWork(), _clock);

        await Assert.ThrowsAsync<NotFoundException>(() => useCase.ExecuteAsync(
            new(PlayerId.New(), notification.Id, true), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => useCase.ExecuteAsync(
            new(recipient, NotificationId.New(), true), CancellationToken.None));
    }

    private static NotificationDraft Draft(PlayerId recipient, string sourceEventKey) => new(
        recipient,
        "platform",
        "friend-request-received",
        "New friend request",
        sourceEventKey,
        "/account/friends",
        sourceEventKey);

    private sealed class CountingUnitOfWork : IUnitOfWork
    {
        public int SaveCount { get; private set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }
}
