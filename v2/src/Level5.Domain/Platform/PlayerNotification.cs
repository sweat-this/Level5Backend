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
    private static readonly Uri PlatformOrigin = new("https://platform.invalid/");

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
            !actionPath.StartsWith('/') ||
            actionPath.StartsWith("//", StringComparison.Ordinal) ||
            actionPath.Contains('\\') ||
            actionPath.Any(char.IsControl) ||
            !Uri.TryCreate(PlatformOrigin, actionPath, out var resolved) ||
            resolved.Scheme != PlatformOrigin.Scheme ||
            resolved.Host != PlatformOrigin.Host ||
            resolved.Port != PlatformOrigin.Port)
        {
            throw new InvalidNotificationException("Notification actionPath must be a safe account path.");
        }

        string decodedPath;
        try
        {
            decodedPath = Uri.UnescapeDataString(resolved.AbsolutePath);
        }
        catch (UriFormatException)
        {
            throw new InvalidNotificationException("Notification actionPath must be a safe account path.");
        }

        if (decodedPath.Contains('\\') ||
            !IsRouteFamily(decodedPath, "/account") ||
            IsRouteFamily(decodedPath, "/account/login") ||
            IsRouteFamily(decodedPath, "/account/register"))
        {
            throw new InvalidNotificationException("Notification actionPath must be a safe account path.");
        }

        return actionPath;
    }

    private static bool IsRouteFamily(string path, string family)
        => path.Equals(family, StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith(family + "/", StringComparison.OrdinalIgnoreCase);
}

public sealed class InvalidNotificationException : DomainException
{
    public override string Code => "invalid_notification";

    public InvalidNotificationException(string message) : base(message)
    {
    }
}
