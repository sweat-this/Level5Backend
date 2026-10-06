using Level5.Domain.Common;
using Level5.Domain.Ids;

namespace Level5.Domain.Platform;

public static class NotificationFieldLimits
{
    public const int Source = 64;
    public const int Kind = 64;
    public const int Title = 160;
    public const int Body = 1000;
    public const int ActionPath = 512;
    public const int SourceEventKey = 128;
}

public sealed class PlayerNotification
{
    public NotificationId Id { get; }
    public PlayerId RecipientPlayerId { get; }
    public string Source { get; }
    public string Kind { get; }
    public string Title { get; }
    public string? Body { get; }
    public string? ActionPath { get; }
    public string SourceEventKey { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? ReadAt { get; private set; }

    private PlayerNotification(
        NotificationId id,
        PlayerId recipientPlayerId,
        string source,
        string kind,
        string title,
        string? body,
        string? actionPath,
        string sourceEventKey,
        DateTimeOffset createdAt,
        DateTimeOffset? readAt)
    {
        Id = id;
        RecipientPlayerId = recipientPlayerId;
        Source = source;
        Kind = kind;
        Title = title;
        Body = body;
        ActionPath = actionPath;
        SourceEventKey = sourceEventKey;
        CreatedAt = createdAt;
        ReadAt = readAt;
    }

    public static PlayerNotification Create(
        PlayerId recipientPlayerId,
        string source,
        string kind,
        string title,
        string? body,
        string? actionPath,
        string sourceEventKey,
        DateTimeOffset createdAt)
        => new(
            NotificationId.New(),
            recipientPlayerId,
            Required(source, NotificationFieldLimits.Source, nameof(source)),
            Required(kind, NotificationFieldLimits.Kind, nameof(kind)),
            Required(title, NotificationFieldLimits.Title, nameof(title)),
            Optional(body, NotificationFieldLimits.Body, nameof(body)),
            ValidateActionPath(actionPath),
            Required(sourceEventKey, NotificationFieldLimits.SourceEventKey, nameof(sourceEventKey)),
            createdAt,
            readAt: null);

    public static PlayerNotification Rehydrate(
        NotificationId id,
        PlayerId recipientPlayerId,
        string source,
        string kind,
        string title,
        string? body,
        string? actionPath,
        string sourceEventKey,
        DateTimeOffset createdAt,
        DateTimeOffset? readAt)
        => new(id, recipientPlayerId, source, kind, title, body, actionPath, sourceEventKey, createdAt, readAt);

    public void SetRead(bool isRead, DateTimeOffset now)
    {
        if (isRead)
        {
            ReadAt ??= now;
        }
        else
        {
            ReadAt = null;
        }
    }

    private static string Required(string value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new InvalidNotificationException($"Notification {field} is invalid.");
        }

        return value;
    }

    private static string? Optional(string? value, int maximumLength, string field)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length > maximumLength)
        {
            throw new InvalidNotificationException($"Notification {field} is invalid.");
        }

        return value;
    }

    private static string? ValidateActionPath(string? actionPath)
    {
        if (actionPath is null)
        {
            return null;
        }

        if (actionPath.Length > NotificationFieldLimits.ActionPath ||
            !(actionPath.Equals("/account", StringComparison.Ordinal) ||
              actionPath.StartsWith("/account/", StringComparison.Ordinal)) ||
            actionPath.Contains('\\') ||
            actionPath.StartsWith("//", StringComparison.Ordinal) ||
            Uri.TryCreate(actionPath, UriKind.Absolute, out _) ||
            actionPath.Equals("/account/login", StringComparison.OrdinalIgnoreCase) ||
            actionPath.StartsWith("/account/login/", StringComparison.OrdinalIgnoreCase) ||
            actionPath.Equals("/account/register", StringComparison.OrdinalIgnoreCase) ||
            actionPath.StartsWith("/account/register/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidNotificationException("Notification actionPath must be a safe account path.");
        }

        return actionPath;
    }
}

public sealed class InvalidNotificationException : DomainException
{
    public override string Code => "invalid_notification";

    public InvalidNotificationException(string message) : base(message)
    {
    }
}
