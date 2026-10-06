using Level5.Domain.Ids;

namespace Level5.Application.Abstractions;

public sealed record NotificationDraft(
    PlayerId RecipientPlayerId,
    string Source,
    string Kind,
    string Title,
    string? Body,
    string? ActionPath,
    string SourceEventKey);

/// <summary>Stages a trusted producer notification in the caller's current unit of work.</summary>
public interface INotificationWriter
{
    Task WriteAsync(NotificationDraft draft, CancellationToken cancellationToken);
}
