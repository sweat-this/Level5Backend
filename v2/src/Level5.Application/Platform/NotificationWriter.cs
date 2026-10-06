using Level5.Application.Abstractions;
using Level5.Domain.Platform;

namespace Level5.Application.Platform;

public sealed class NotificationWriter(INotificationStore store, IClock clock) : INotificationWriter
{
    public Task WriteAsync(NotificationDraft draft, CancellationToken cancellationToken)
        => store.AddAsync(PlayerNotification.Create(
            draft.RecipientPlayerId,
            draft.Source,
            draft.Kind,
            draft.Title,
            draft.Body,
            draft.ActionPath,
            draft.SourceEventKey,
            clock.UtcNow), cancellationToken);
}
