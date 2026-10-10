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
    /// <summary>Checks durable bucket identity, independently of inbox read state.
    /// Producers must serialize existence checks and staging for their source event.</summary>
    Task<bool> ExistsAsync(PlayerId recipient, string source, string sourceEventKey, CancellationToken cancellationToken);
    Task WriteAsync(NotificationDraft draft, CancellationToken cancellationToken);
}
