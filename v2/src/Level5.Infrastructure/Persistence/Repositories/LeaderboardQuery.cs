using System.Runtime.CompilerServices;
using Level5.Application.Abstractions;
using Level5.Domain.Leaderboards;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.Persistence.Repositories;

/// <summary>
/// Ranks <c>match_results</c> directly in PostgreSQL (issue: leaderboard reads) - the ranking
/// metric's value is extracted from the JSONB <c>MetricsJson</c> document in an inner query
/// (aliased below so the outer query can filter/order/keyset-paginate on it without re-extracting
/// it per predicate), joined once to <c>player_profiles</c> for the public identity every row
/// needs. Never loads a candidate row set into memory to sort it, and never deserializes a full
/// <see cref="Level5.Domain.Results.MatchResult"/> aggregate - only the scalar columns a
/// leaderboard row actually needs are selected.
/// </summary>
public sealed class LeaderboardQuery(Level5V2DbContext db) : ILeaderboardQuery
{
    public async Task<IReadOnlyList<LeaderboardRow>> ExecuteAsync(LeaderboardQuerySpec spec, CancellationToken cancellationToken)
    {
        var args = new List<object?>();
        string P(object? value)
        {
            args.Add(value);
            return "{" + (args.Count - 1) + "}";
        }

        var metricKey = spec.RankingMetric.ToString();
        var metricKeyPlaceholder = P(metricKey);
        var modeIdPlaceholder = P(spec.ModeId);
        var hardcorePlaceholder1 = P(spec.Hardcore);
        var hardcorePlaceholder2 = P(spec.Hardcore);
        var trafficPlaceholder1 = P(spec.TrafficEnabled);
        var trafficPlaceholder2 = P(spec.TrafficEnabled);
        var enemiesPlaceholder1 = P(spec.EnemiesEnabled);
        var enemiesPlaceholder2 = P(spec.EnemiesEnabled);
        var sniperPlaceholder1 = P(spec.SniperEnabled);
        var sniperPlaceholder2 = P(spec.SniperEnabled);
        var hasCursorPlaceholder = P(spec.Cursor is not null);
        var cursorValuePlaceholder1 = P(spec.Cursor?.RankingValue ?? 0d);
        var cursorValuePlaceholder2 = P(spec.Cursor?.RankingValue ?? 0d);
        var cursorCreatedAtPlaceholder1 = P(spec.Cursor?.CreatedAt ?? DateTimeOffset.UnixEpoch);
        var cursorCreatedAtPlaceholder2 = P(spec.Cursor?.CreatedAt ?? DateTimeOffset.UnixEpoch);
        var cursorIdPlaceholder = P(spec.Cursor?.MatchResultId ?? Guid.Empty);
        var limitPlaceholder = P(spec.Limit + 1);

        // Postgres has no implicit cast between the two: HigherWins reads "strictly better than the
        // cursor row" as "smaller ranking value" once already-returned higher values are excluded by
        // descending order, and vice versa for LowerWins - this is a plain literal keyword choice made
        // in C#, never interpolated client/user data, so it carries no injection risk.
        var higherWins = spec.Direction == RankingDirection.HigherWins;
        var cursorComparisonOperator = higherWins ? "<" : ">";
        var orderDirection = higherWins ? "DESC" : "ASC";

        var sql = $"""
            SELECT * FROM (
                SELECT
                    mr."Id" AS "MatchResultId",
                    mr."PlayerId" AS "PlayerId",
                    pp."DisplayName" AS "DisplayName",
                    pp."Tag" AS "Tag",
                    mr."CharacterId" AS "CharacterId",
                    mr."LevelId" AS "LevelId",
                    (mr."MetricsJson" ->> {metricKeyPlaceholder})::double precision AS "RankingValue",
                    mr."CreatedAt" AS "CreatedAt",
                    -- COALESCEd for the same reason the outer query drops rows with no ranking
                    -- value: a modifier key missing from the document would otherwise materialize
                    -- SQL NULL into LeaderboardRow's non-nullable bools and fail the entire board
                    -- rather than the one malformed row. MatchResultStore always writes all four,
                    -- so this is containment for data that should not exist, not a normal path.
                    COALESCE((mr."ModifiersJson" ->> 'Hardcore')::boolean, false) AS "Hardcore",
                    COALESCE((mr."ModifiersJson" ->> 'TrafficEnabled')::boolean, false) AS "TrafficEnabled",
                    COALESCE((mr."ModifiersJson" ->> 'EnemiesEnabled')::boolean, false) AS "EnemiesEnabled",
                    COALESCE((mr."ModifiersJson" ->> 'SniperEnabled')::boolean, false) AS "SniperEnabled"
                FROM match_results mr
                JOIN player_profiles pp ON pp."Id" = mr."PlayerId"
                WHERE mr."ModeId" = {modeIdPlaceholder}
                  AND ({hardcorePlaceholder1}::boolean IS NULL OR (mr."ModifiersJson" ->> 'Hardcore')::boolean = {hardcorePlaceholder2}::boolean)
                  AND ({trafficPlaceholder1}::boolean IS NULL OR (mr."ModifiersJson" ->> 'TrafficEnabled')::boolean = {trafficPlaceholder2}::boolean)
                  AND ({enemiesPlaceholder1}::boolean IS NULL OR (mr."ModifiersJson" ->> 'EnemiesEnabled')::boolean = {enemiesPlaceholder2}::boolean)
                  AND ({sniperPlaceholder1}::boolean IS NULL OR (mr."ModifiersJson" ->> 'SniperEnabled')::boolean = {sniperPlaceholder2}::boolean)
            ) ranked
            WHERE "RankingValue" IS NOT NULL
              AND (NOT {hasCursorPlaceholder} OR
                   "RankingValue" {cursorComparisonOperator} {cursorValuePlaceholder1}
                   OR ("RankingValue" = {cursorValuePlaceholder2}
                       AND ("CreatedAt" > {cursorCreatedAtPlaceholder1}
                            OR ("CreatedAt" = {cursorCreatedAtPlaceholder2} AND "MatchResultId" > {cursorIdPlaceholder}))))
            ORDER BY "RankingValue" {orderDirection}, "CreatedAt" ASC, "MatchResultId" ASC
            LIMIT {limitPlaceholder}
            """;

        var formattable = FormattableStringFactory.Create(sql, [.. args]);

        var rows = await db.Database.SqlQuery<LeaderboardRow>(formattable).ToListAsync(cancellationToken);
        return rows;
    }
}
