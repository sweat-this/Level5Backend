namespace Level5.Application.Competition;

/// <summary>
/// Shared bounded-pagination contract for the correspondence list endpoints (issue #21):
/// a caller-supplied page size is honored up to <see cref="MaxLimit"/>, never beyond it, and a
/// missing/non-positive value falls back to <see cref="DefaultLimit"/>. Kept here (Application),
/// not duplicated in Infrastructure or Api, so the store's actual query and the controller's
/// documented contract can never drift apart.
/// </summary>
public static class SeriesListPaging
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    /// <summary>
    /// <see cref="Common.KeysetCursor"/> scope tags - one per list query, so a cursor issued by one
    /// can never be silently reinterpreted against another's sort key/ordering.
    /// </summary>
    public const string IncomingScope = "incoming";
    public const string OutgoingScope = "outgoing";
    public const string ActiveScope = "active";
    public const string CompletedScope = "completed";
    public const string HistoryScope = "history";

    public static int ResolveLimit(int? requested) => requested is null or <= 0 ? DefaultLimit : Math.Min(requested.Value, MaxLimit);
}
