using Level5.Domain.Ids;
using Level5.Domain.Platform;

namespace Level5.Application.Abstractions;

public sealed record NotificationCursorPosition(DateTimeOffset CreatedAt, NotificationId Id);

public interface INotificationStore
{
    Task<bool> ExistsAsync(PlayerId recipient, string source, string sourceEventKey, CancellationToken cancellationToken);
    Task AddAsync(PlayerNotification notification, CancellationToken cancellationToken);
    Task<IReadOnlyList<PlayerNotification>> ListAsync(
        PlayerId recipientPlayerId,
        NotificationCursorPosition? cursor,
        int take,
        CancellationToken cancellationToken);
    Task<PlayerNotification?> FindAsync(
        PlayerId recipientPlayerId,
        NotificationId notificationId,
        CancellationToken cancellationToken);
    Task StageUpdateAsync(PlayerNotification notification, CancellationToken cancellationToken);
}
