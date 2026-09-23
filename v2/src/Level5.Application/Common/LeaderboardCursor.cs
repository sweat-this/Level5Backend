using System.Globalization;
using System.Text;

namespace Level5.Application.Common;

/// <summary>
/// Opaque keyset-pagination cursor for leaderboard reads: a (ranking value, CreatedAt, MatchResultId)
/// triple, base64-encoded so it is meaningless to a caller and safe to round-trip through a query
/// string. Sibling to <see cref="KeysetCursor"/> rather than a reuse of it - a leaderboard row's
/// primary sort key is a numeric metric value the correspondence list cursors have no equivalent
/// of, so this carries one extra component instead of overloading that type's two-part shape.
/// </summary>
public static class LeaderboardCursor
{
    /// <param name="scope">
    /// Identifies which leaderboard query issued the cursor - built by the caller from the mode id
    /// plus every active modifier filter (see <c>GetLeaderboardUseCase</c>), so a cursor from one
    /// board/filter combination can never be silently reinterpreted against another's ranking
    /// metric, sort direction, or filtered row set. <see cref="Decode"/> rejects a mismatch the
    /// same way it rejects a garbled cursor, rather than producing a plausible-looking but
    /// semantically wrong page.
    /// </param>
    public static string Encode(string scope, double rankingValue, DateTimeOffset createdAt, Guid id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{scope}:{rankingValue.ToString("R", CultureInfo.InvariantCulture)}:{createdAt.UtcTicks}:{id}"));

    /// <summary>
    /// Throws <see cref="ValidationFailedException"/> (400) for anything that is not a cursor this
    /// codec produced for this exact <paramref name="scope"/> - including a well-formed cursor
    /// from a different board or filter combination.
    /// </summary>
    public static (double RankingValue, DateTimeOffset CreatedAt, Guid Id) Decode(string scope, string cursor)
    {
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));

            // Split from the right for the three fixed trailing fields, not from the left with a
            // capped count - a caller's scope (e.g. GetLeaderboardUseCase's mode+filters string) is
            // not guaranteed to be colon-free, and a left-anchored split would silently mistake part
            // of the scope for the ranking value on any scope that happened to contain one.
            var parts = raw.Split(':');
            if (parts.Length < 4)
            {
                throw new FormatException("Cursor is missing required fields.");
            }

            var actualScope = string.Join(':', parts[..^3]);
            if (actualScope != scope)
            {
                throw new FormatException("Cursor scope does not match this leaderboard query.");
            }

            var rankingValue = double.Parse(parts[^3], NumberStyles.Float, CultureInfo.InvariantCulture);
            var ticks = long.Parse(parts[^2], CultureInfo.InvariantCulture);
            var id = Guid.Parse(parts[^1]);
            return (rankingValue, new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new ValidationFailedException("The provided pagination cursor is invalid.");
        }
    }
}
