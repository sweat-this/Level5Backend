using System.Runtime.CompilerServices;
using Npgsql;

namespace Level5.LegacyScoreMigration.Legacy;

/// <summary>
/// Reads V1 <c>highscores</c> rows directly over raw Npgsql SQL - never via the legacy EF model or
/// Level5Backend.csproj (architecturally forbidden; see DependencyRuleTests). Read-only: this class
/// never issues an INSERT/UPDATE/DELETE against V1. Column names are quoted exactly as
/// <c>Models/Level5Context.cs</c> declares them - unlike V1's <c>users</c> table, several
/// <c>highscores</c> columns are camelCase and therefore case-sensitive in Postgres.
/// </summary>
public sealed class LegacyHighscoreReader(string legacyConnectionString)
{
    private const string Columns =
        """
        id, userid, scoreid, modeid, levelid, characterid, version, platform,
        "totalPoints", "maxShotMade", "totalDistance", time, "consecutiveShots", "enemiesKilled",
        "hardcoreEnabled", "trafficEnabled", "enemiesEnabled", "sniperEnabled", date
        """;

    private readonly string _selectAllSql = $"SELECT {Columns} FROM highscores ORDER BY id";
    private readonly string _selectOneSql = $"SELECT {Columns} FROM highscores WHERE id = @id";

    public async IAsyncEnumerable<LegacyHighscoreRecord> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(legacyConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(_selectAllSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            yield return Map(reader);
        }
    }

    public async Task<LegacyHighscoreRecord?> ReadOneAsync(int legacyHighscoreId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(legacyConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(_selectOneSql, connection);
        command.Parameters.AddWithValue("id", legacyHighscoreId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    private static LegacyHighscoreRecord Map(NpgsqlDataReader reader) => new(
        Id: reader.GetInt32(reader.GetOrdinal("id")),
        Userid: reader.GetInt32(reader.GetOrdinal("userid")),
        Scoreid: reader.IsDBNull(reader.GetOrdinal("scoreid")) ? null : reader.GetString(reader.GetOrdinal("scoreid")),
        Modeid: reader.GetInt32(reader.GetOrdinal("modeid")),
        Levelid: reader.GetInt32(reader.GetOrdinal("levelid")),
        Characterid: reader.GetInt32(reader.GetOrdinal("characterid")),
        Version: reader.IsDBNull(reader.GetOrdinal("version")) ? null : reader.GetString(reader.GetOrdinal("version")),
        Platform: reader.IsDBNull(reader.GetOrdinal("platform")) ? null : reader.GetString(reader.GetOrdinal("platform")),
        TotalPoints: reader.GetInt32(reader.GetOrdinal("totalPoints")),
        MaxShotMade: reader.GetInt32(reader.GetOrdinal("maxShotMade")),
        TotalDistance: reader.GetFloat(reader.GetOrdinal("totalDistance")),
        Time: reader.GetFloat(reader.GetOrdinal("time")),
        ConsecutiveShots: reader.GetInt32(reader.GetOrdinal("consecutiveShots")),
        EnemiesKilled: reader.GetInt32(reader.GetOrdinal("enemiesKilled")),
        HardcoreEnabled: reader.GetInt32(reader.GetOrdinal("hardcoreEnabled")),
        TrafficEnabled: reader.GetInt32(reader.GetOrdinal("trafficEnabled")),
        EnemiesEnabled: reader.GetInt32(reader.GetOrdinal("enemiesEnabled")),
        SniperEnabled: reader.GetInt32(reader.GetOrdinal("sniperEnabled")),
        Date: reader.IsDBNull(reader.GetOrdinal("date")) ? null : reader.GetString(reader.GetOrdinal("date")));
}
