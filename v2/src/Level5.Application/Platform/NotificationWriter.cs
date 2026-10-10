using Level5.Application.Abstractions;
using Level5.Domain.Platform;
using Level5.Domain.Ids;

namespace Level5.Application.Platform;

public sealed class NotificationWriter(INotificationStore store, IClock clock) : INotificationWriter
{
    public Task<bool> ExistsAsync(PlayerId recipient, string source, string sourceEventKey, CancellationToken cancellationToken)
        => store.ExistsAsync(recipient, source, sourceEventKey, cancellationToken);

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
