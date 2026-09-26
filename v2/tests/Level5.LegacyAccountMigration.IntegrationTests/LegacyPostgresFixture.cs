using Npgsql;
using Testcontainers.PostgreSql;

namespace Level5.LegacyAccountMigration.IntegrationTests;

/// <summary>
/// One real, ephemeral Postgres container shaped like V1's schema - hand-rolled (not from
/// Level5Backend's own EF migrations), because referencing Level5Backend.csproj's migrations from
/// a V2 test project is exactly what's architecturally forbidden, even in tests. Matches
/// Models/User.cs's exact `users` table shape as of dev @ c6bed91f.
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
            CREATE TABLE users (
                userid SERIAL PRIMARY KEY,
                username varchar(45) NOT NULL,
                password varchar(255) NOT NULL,
                email varchar(45) NULL,
                firstname varchar(45) NULL,
                lastname varchar(45) NULL,
                ipaddress varchar(45) NULL,
                signupdate varchar(45) NULL,
                lastlogin varchar(45) NULL,
                isdev int NULL
            );
            CREATE UNIQUE INDEX "IX_users_username" ON users (username);
            """, connection);
        await create.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>Test-only seeding helper - never used by the tool itself, which is read-only against V1.</summary>
    public async Task<int> InsertUserAsync(string username, string password, string? email = null, int? isDev = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var insert = new NpgsqlCommand(
            "INSERT INTO users (username, password, email, isdev) VALUES (@username, @password, @email, @isdev) RETURNING userid",
            connection);
        insert.Parameters.AddWithValue("username", username);
        insert.Parameters.AddWithValue("password", password);
        insert.Parameters.AddWithValue("email", (object?)email ?? DBNull.Value);
        insert.Parameters.AddWithValue("isdev", (object?)isDev ?? DBNull.Value);

        return (int)(await insert.ExecuteScalarAsync())!;
    }

    /// <summary>Test-only helper to assert V1 row counts never change (the tool must never write to V1).</summary>
    public async Task<long> CountUsersAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var count = new NpgsqlCommand("SELECT COUNT(*) FROM users", connection);
        return (long)(await count.ExecuteScalarAsync())!;
    }
}
