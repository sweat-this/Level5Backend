using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Domain.Platform;

namespace Level5.Application.Tests.Fakes;

public sealed class InMemoryNotificationStore : INotificationStore
{
    private readonly List<PlayerNotification> _notifications = [];

    public IReadOnlyList<PlayerNotification> Notifications => _notifications;

    public Task<bool> ExistsAsync(PlayerId recipient, string source, string sourceEventKey, CancellationToken cancellationToken)
        => Task.FromResult(_notifications.Any(item => item.RecipientPlayerId == recipient &&
            item.Source == source && item.SourceEventKey == sourceEventKey));

    public Task AddAsync(PlayerNotification notification, CancellationToken cancellationToken)
    {
        if (_notifications.Any(existing =>
                existing.RecipientPlayerId == notification.RecipientPlayerId &&
                existing.Source == notification.Source &&
                existing.SourceEventKey == notification.SourceEventKey))
        {
            throw new InvalidOperationException("Duplicate notification source event.");
        }

        _notifications.Add(notification);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PlayerNotification>> ListAsync(
        PlayerId recipientPlayerId,
        NotificationCursorPosition? cursor,
        int take,
        CancellationToken cancellationToken)
    {
        IEnumerable<PlayerNotification> query = _notifications
            .Where(notification => notification.RecipientPlayerId == recipientPlayerId);
        if (cursor is not null)
        {
            query = query.Where(notification =>
                notification.CreatedAt < cursor.CreatedAt ||
                (notification.CreatedAt == cursor.CreatedAt &&
                 notification.Id.Value.CompareTo(cursor.Id.Value) < 0));
        }

        IReadOnlyList<PlayerNotification> result = query
            .OrderByDescending(notification => notification.CreatedAt)
            .ThenByDescending(notification => notification.Id.Value)
            .Take(take)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<PlayerNotification?> FindAsync(
        PlayerId recipientPlayerId,
        NotificationId notificationId,
        CancellationToken cancellationToken)
        => Task.FromResult(_notifications.SingleOrDefault(notification =>
            notification.RecipientPlayerId == recipientPlayerId && notification.Id == notificationId));

    public Task StageUpdateAsync(PlayerNotification notification, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
