using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Domain.Platform;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.Persistence.Repositories;

public sealed class NotificationStore(Level5V2DbContext db) : INotificationStore
{
    public Task AddAsync(PlayerNotification notification, CancellationToken cancellationToken)
        => db.PlayerNotifications.AddAsync(ToRow(notification), cancellationToken).AsTask();

    public async Task<IReadOnlyList<PlayerNotification>> ListAsync(
        PlayerId recipientPlayerId,
        NotificationCursorPosition? cursor,
        int take,
        CancellationToken cancellationToken)
    {
        var query = db.PlayerNotifications
            .AsNoTracking()
            .Where(row => row.RecipientPlayerId == recipientPlayerId.Value);

        if (cursor is not null)
        {
            query = query.Where(row =>
                row.CreatedAt < cursor.CreatedAt ||
                (row.CreatedAt == cursor.CreatedAt && row.Id.CompareTo(cursor.Id.Value) < 0));
        }

        var rows = await query
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(ToDomain)];
    }

    public async Task<PlayerNotification?> FindAsync(
        PlayerId recipientPlayerId,
        NotificationId notificationId,
        CancellationToken cancellationToken)
    {
        var row = await db.PlayerNotifications.SingleOrDefaultAsync(
            row => row.RecipientPlayerId == recipientPlayerId.Value && row.Id == notificationId.Value,
            cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task StageUpdateAsync(PlayerNotification notification, CancellationToken cancellationToken)
    {
        var row = await db.PlayerNotifications.SingleAsync(
            row => row.Id == notification.Id.Value &&
                   row.RecipientPlayerId == notification.RecipientPlayerId.Value,
            cancellationToken);
        row.ReadAt = notification.ReadAt;
    }

    private static PlayerNotificationRow ToRow(PlayerNotification notification) => new()
    {
        Id = notification.Id.Value,
        RecipientPlayerId = notification.RecipientPlayerId.Value,
        Source = notification.Source,
        Kind = notification.Kind,
        Title = notification.Title,
        Body = notification.Body,
        ActionPath = notification.ActionPath,
        SourceEventKey = notification.SourceEventKey,
        CreatedAt = notification.CreatedAt,
        ReadAt = notification.ReadAt
    };

    private static PlayerNotification ToDomain(PlayerNotificationRow row) => PlayerNotification.Rehydrate(
        new NotificationId(row.Id),
        new PlayerId(row.RecipientPlayerId),
        row.Source,
        row.Kind,
        row.Title,
        row.Body,
        row.ActionPath,
        row.SourceEventKey,
        row.CreatedAt,
        row.ReadAt);
}
