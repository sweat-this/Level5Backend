namespace Level5.Application.Leaderboards;

/// <summary>
/// Shared bounded-pagination contract for leaderboard reads, mirroring
/// <see cref="Level5.Application.Competition.SeriesListPaging"/>'s established convention: a
/// caller-supplied page size is honored up to <see cref="MaxLimit"/>, never beyond it, and a
/// missing/non-positive value falls back to <see cref="DefaultLimit"/>.
/// </summary>
public static class LeaderboardPaging
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public static int ResolveLimit(int? requested) => requested is null or <= 0 ? DefaultLimit : Math.Min(requested.Value, MaxLimit);
}
