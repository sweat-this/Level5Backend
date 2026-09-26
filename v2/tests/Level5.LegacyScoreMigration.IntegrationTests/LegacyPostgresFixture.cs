using Npgsql;
using Testcontainers.PostgreSql;

namespace Level5.LegacyScoreMigration.IntegrationTests;

/// <summary>
/// One real, ephemeral Postgres container shaped like V1's <c>highscores</c> schema - hand-rolled
/// (not from Level5Backend's own EF migrations), because referencing Level5Backend.csproj's
/// migrations from a V2 test project is exactly what's architecturally forbidden, even in tests.
/// Matches <c>Models/Highscore.cs</c> + <c>Models/Level5Context.cs</c>'s exact column names/types as
/// of dev @ 1b905fdc9 - including the camelCase, case-sensitive columns the real table has. Only
/// the columns this migration tool actually reads or that are NOT NULL in V1 are declared; every
/// V1-only telemetry/breakdown column this migration deliberately never touches is omitted.
/// </summary>
public sealed class LegacyPostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgres:18-alpine")
            .WithDatabase("level5_test")
            .WithUsername("level5")
            .WithPassword("test-password")
            .Build();

        await _container.StartAsync();

        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();

        await using var create = new NpgsqlCommand(
            """
            CREATE TABLE highscores (
                id SERIAL PRIMARY KEY,
                userid integer NOT NULL,
                scoreid varchar(100) NULL,
                modeid integer NOT NULL,
                characterid integer NOT NULL,
                levelid integer NOT NULL,
                character varchar(45) NOT NULL,
                level varchar(45) NOT NULL,
                os varchar(45) NOT NULL,
                version varchar(45) NULL,
                date varchar(45) NULL,
                time real NOT NULL,
                "totalPoints" integer NOT NULL,
                "totalDistance" real NOT NULL,
                "consecutiveShots" integer NOT NULL,
                "trafficEnabled" integer NOT NULL,
                "hardcoreEnabled" integer NOT NULL,
                "enemiesEnabled" integer NOT NULL,
                "enemiesKilled" integer NOT NULL,
                "sniperEnabled" integer NOT NULL,
                "maxShotMade" integer NOT NULL,
                platform varchar(45) NULL
            );
            """, connection);
        await create.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>Test-only seeding helper - never used by the tool itself, which is read-only against V1.</summary>
    public async Task<int> InsertHighscoreAsync(
        int userid,
        string? scoreid,
        int modeid = 1,
        int levelid = 1,
        int characterid = 1,
        string? version = "1.0.0",
        string? platform = "Handheld",
        int totalPoints = 100,
        int maxShotMade = 10,
        float totalDistance = 50.5f,
        float time = 30.2f,
        int consecutiveShots = 3,
        int enemiesKilled = 0,
        int hardcoreEnabled = 0,
        int trafficEnabled = 0,
        int enemiesEnabled = 0,
        int sniperEnabled = 0,
        string? date = "9/25/2026 1:23:45 PM")
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO highscores
                (userid, scoreid, modeid, levelid, characterid, character, level, os, version, date,
                 time, "totalPoints", "totalDistance", "consecutiveShots", "trafficEnabled",
                 "hardcoreEnabled", "enemiesEnabled", "enemiesKilled", "sniperEnabled", "maxShotMade", platform)
            VALUES
                (@userid, @scoreid, @modeid, @levelid, @characterid, 'char', 'level', 'os', @version, @date,
                 @time, @totalPoints, @totalDistance, @consecutiveShots, @trafficEnabled,
                 @hardcoreEnabled, @enemiesEnabled, @enemiesKilled, @sniperEnabled, @maxShotMade, @platform)
            RETURNING id
            """, connection);
        insert.Parameters.AddWithValue("userid", userid);
        insert.Parameters.AddWithValue("scoreid", (object?)scoreid ?? DBNull.Value);
        insert.Parameters.AddWithValue("modeid", modeid);
        insert.Parameters.AddWithValue("levelid", levelid);
        insert.Parameters.AddWithValue("characterid", characterid);
        insert.Parameters.AddWithValue("version", (object?)version ?? DBNull.Value);
        insert.Parameters.AddWithValue("date", (object?)date ?? DBNull.Value);
        insert.Parameters.AddWithValue("time", time);
        insert.Parameters.AddWithValue("totalPoints", totalPoints);
        insert.Parameters.AddWithValue("totalDistance", totalDistance);
        insert.Parameters.AddWithValue("consecutiveShots", consecutiveShots);
        insert.Parameters.AddWithValue("trafficEnabled", trafficEnabled);
        insert.Parameters.AddWithValue("hardcoreEnabled", hardcoreEnabled);
        insert.Parameters.AddWithValue("enemiesEnabled", enemiesEnabled);
        insert.Parameters.AddWithValue("enemiesKilled", enemiesKilled);
        insert.Parameters.AddWithValue("sniperEnabled", sniperEnabled);
        insert.Parameters.AddWithValue("maxShotMade", maxShotMade);
        insert.Parameters.AddWithValue("platform", (object?)platform ?? DBNull.Value);

        return (int)(await insert.ExecuteScalarAsync())!;
    }

    /// <summary>Test-only helper to assert V1 row counts never change (the tool must never write to V1).</summary>
    public async Task<long> CountHighscoresAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var count = new NpgsqlCommand("SELECT COUNT(*) FROM highscores", connection);
        return (long)(await count.ExecuteScalarAsync())!;
    }
}
