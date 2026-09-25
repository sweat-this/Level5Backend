using System.Runtime.CompilerServices;
using Npgsql;

namespace Level5.LegacyAccountMigration.Legacy;

/// <summary>
/// Reads V1 <c>users</c> rows directly over raw Npgsql SQL - never via the legacy EF model or
/// Level5Backend.csproj (architecturally forbidden; see DependencyRuleTests). Read-only: this
/// class never issues an INSERT/UPDATE/DELETE against V1.
/// </summary>
public sealed class LegacyUserReader(string legacyConnectionString)
{
    private const string SelectAllSql =
        "SELECT userid, username, password, email, isdev FROM users ORDER BY userid";

    private const string SelectOneSql =
        "SELECT userid, username, password, email, isdev FROM users WHERE userid = @userId";

    public async IAsyncEnumerable<LegacyUserRecord> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(legacyConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(SelectAllSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            yield return Map(reader);
        }
    }

    public async Task<LegacyUserRecord?> ReadOneAsync(int userId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(legacyConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(SelectOneSql, connection);
        command.Parameters.AddWithValue("userId", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    private static LegacyUserRecord Map(NpgsqlDataReader reader) => new(
        UserId: reader.GetInt32(reader.GetOrdinal("userid")),
        Username: reader.GetString(reader.GetOrdinal("username")),
        Password: reader.GetString(reader.GetOrdinal("password")),
        Email: reader.IsDBNull(reader.GetOrdinal("email")) ? null : reader.GetString(reader.GetOrdinal("email")),
        IsDev: reader.IsDBNull(reader.GetOrdinal("isdev")) ? null : reader.GetInt32(reader.GetOrdinal("isdev")));
}
