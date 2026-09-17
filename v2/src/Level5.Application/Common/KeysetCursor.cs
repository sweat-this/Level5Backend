using System.Text;

namespace Level5.Application.Common;

/// <summary>
/// Opaque keyset-pagination cursor shared by every correspondence list query (issue #21): a
/// (sort key, tiebreaker id) pair, base64-encoded so it is meaningless to a caller and safe to
/// round-trip through a query string. One codec for both the real store and the in-memory test
/// double, so their pagination semantics cannot silently drift apart.
/// </summary>
public static class KeysetCursor
{
    /// <param name="scope">
    /// Identifies which list query issued the cursor (e.g. "active" vs "completed" - see
    /// <c>SeriesListPaging</c>'s scope constants). Embedded so a cursor from one list can never be
    /// silently reinterpreted against a different one's sort key/ordering - <see cref="Decode"/>
    /// rejects a mismatch the same way it rejects a garbled cursor, rather than producing a
    /// plausible-looking but semantically wrong page.
    /// </param>
    public static string Encode(string scope, DateTimeOffset sortKey, Guid id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{scope}:{sortKey.UtcTicks}:{id}"));

    /// <summary>
    /// Throws <see cref="ValidationFailedException"/> (400) for anything that is not a cursor this
    /// codec produced for this exact <paramref name="scope"/> - including a well-formed cursor
    /// from a different list query.
    /// </summary>
    public static (DateTimeOffset SortKey, Guid Id) Decode(string scope, string cursor)
    {
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split(':', 3);
            if (parts.Length != 3 || parts[0] != scope)
            {
                throw new FormatException("Cursor scope does not match this query.");
            }

            var ticks = long.Parse(parts[1]);
            var id = Guid.Parse(parts[2]);
            return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new ValidationFailedException("The provided pagination cursor is invalid.");
        }
    }
}
