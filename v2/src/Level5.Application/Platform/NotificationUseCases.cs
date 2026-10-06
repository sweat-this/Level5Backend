using System.Text;
using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Ids;

namespace Level5.Application.Platform;

public sealed record ListNotificationsRequest(PlayerId PlayerId, int? Limit, string? Cursor);

public sealed record NotificationView(
    NotificationId Id,
    string Source,
    string Kind,
    string Title,
    string? Body,
    string? ActionPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

public sealed record NotificationPage(IReadOnlyList<NotificationView> Items, string? NextCursor);

public sealed class ListNotificationsUseCase(INotificationStore store)
{
    public const int DefaultLimit = 20;
    public const int MaximumLimit = 100;

    public async Task<NotificationPage> ExecuteAsync(ListNotificationsRequest request, CancellationToken cancellationToken)
    {
        var limit = request.Limit ?? DefaultLimit;
        if (limit is < 1 or > MaximumLimit)
        {
            throw new ValidationFailedException($"Limit must be between 1 and {MaximumLimit}.");
        }

        var cursor = request.Cursor is null ? null : NotificationCursor.Decode(request.Cursor);
        var rows = await store.ListAsync(request.PlayerId, cursor, limit + 1, cancellationToken);
        var page = rows.Take(limit).ToList();
        var nextCursor = rows.Count > limit
            ? NotificationCursor.Encode(page[^1].CreatedAt, page[^1].Id)
            : null;

        return new NotificationPage([.. page.Select(ToView)], nextCursor);
    }

    private static NotificationView ToView(Domain.Platform.PlayerNotification notification) => new(
        notification.Id,
        notification.Source,
        notification.Kind,
        notification.Title,
        notification.Body,
        notification.ActionPath,
        notification.CreatedAt,
        notification.ReadAt);
}

public sealed record SetNotificationReadStateRequest(PlayerId PlayerId, NotificationId NotificationId, bool IsRead);

public sealed class SetNotificationReadStateUseCase(
    INotificationStore store,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task ExecuteAsync(SetNotificationReadStateRequest request, CancellationToken cancellationToken)
    {
        var notification = await store.FindAsync(request.PlayerId, request.NotificationId, cancellationToken)
            ?? throw new NotFoundException("Notification was not found.");

        notification.SetRead(request.IsRead, clock.UtcNow);
        await store.StageUpdateAsync(notification, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

internal static class NotificationCursor
{
    private const string Scope = "notifications-v1";

    public static string Encode(DateTimeOffset createdAt, NotificationId id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Scope}:{createdAt.UtcTicks}:{id.Value}"));

    public static NotificationCursorPosition Decode(string cursor)
    {
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split(':', 3);
            if (parts.Length != 3 || parts[0] != Scope)
            {
                throw new FormatException();
            }

            return new NotificationCursorPosition(
                new DateTimeOffset(long.Parse(parts[1]), TimeSpan.Zero),
                new NotificationId(Guid.Parse(parts[2])));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new ValidationFailedException("The provided pagination cursor is invalid.");
        }
    }
}
