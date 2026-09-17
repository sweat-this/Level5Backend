using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

/// <summary>
/// Asserts directly on the schema the migrations produce, rather than only on behavior that
/// happens to depend on it - so a constraint silently dropped from a future migration fails here
/// even if no other test happens to exercise it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SchemaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migrations_create_accounts_and_player_profiles_tables()
    {
        await using var db = fixture.CreateDbContext();

        var tables = await db.Database.SqlQuery<string>(
                $"SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'")
            .ToListAsync();

        Assert.Contains("accounts", tables);
        Assert.Contains("player_profiles", tables);
        Assert.Contains("auth_sessions", tables);
    }

    [Fact]
    public async Task Auth_sessions_has_a_restrictive_foreign_key_to_accounts()
    {
        await using var db = fixture.CreateDbContext();

        var deleteRules = await db.Database.SqlQuery<string>(
                $"""
                 SELECT rc.delete_rule
                 FROM information_schema.referential_constraints rc
                 JOIN information_schema.table_constraints tc
                   ON tc.constraint_name = rc.constraint_name
                 WHERE tc.table_name = 'auth_sessions'
                 """)
            .ToListAsync();

        var deleteRule = Assert.Single(deleteRules);
        Assert.Equal("RESTRICT", deleteRule);
    }

    [Fact]
    public async Task Auth_sessions_has_a_unique_index_on_the_refresh_token_hash()
    {
        await using var db = fixture.CreateDbContext();

        var indexNames = await db.Database.SqlQuery<string>(
                $"""
                 SELECT indexname FROM pg_indexes
                 WHERE tablename = 'auth_sessions' AND indexdef LIKE '%UNIQUE%' AND indexdef LIKE '%RefreshTokenHash%'
                 """)
            .ToListAsync();

        Assert.Single(indexNames);
    }

    [Fact]
    public async Task Player_profiles_has_a_restrictive_foreign_key_to_accounts()
    {
        await using var db = fixture.CreateDbContext();

        var deleteRules = await db.Database.SqlQuery<string>(
                $"""
                 SELECT rc.delete_rule
                 FROM information_schema.referential_constraints rc
                 JOIN information_schema.table_constraints tc
                   ON tc.constraint_name = rc.constraint_name
                 WHERE tc.table_name = 'player_profiles'
                 """)
            .ToListAsync();

        var deleteRule = Assert.Single(deleteRules);
        Assert.Equal("RESTRICT", deleteRule);
    }

    [Fact]
    public async Task No_migrations_are_pending_after_applying_the_full_chain_from_an_empty_database()
    {
        await using var db = fixture.CreateDbContext();

        var pending = await db.Database.GetPendingMigrationsAsync();

        Assert.Empty(pending);
    }

    [Fact]
    public async Task Friend_requests_has_a_unique_partial_index_on_the_canonical_pending_pair()
    {
        await using var db = fixture.CreateDbContext();

        var indexDefs = await db.Database.SqlQuery<string>(
                $"""
                 SELECT indexdef FROM pg_indexes
                 WHERE tablename = 'friend_requests' AND indexname = 'IX_friend_requests_LowerPlayerId_UpperPlayerId'
                 """)
            .ToListAsync();

        var indexDef = Assert.Single(indexDefs);
        Assert.Contains("UNIQUE", indexDef);
        Assert.Contains("Status", indexDef);
        Assert.Contains("Pending", indexDef);
    }

    [Fact]
    public async Task Friend_requests_has_a_revision_column_for_optimistic_concurrency()
    {
        await using var db = fixture.CreateDbContext();

        var columns = await db.Database.SqlQuery<string>(
                $"""
                 SELECT column_name FROM information_schema.columns
                 WHERE table_name = 'friend_requests' AND column_name = 'Revision'
                 """)
            .ToListAsync();

        Assert.Single(columns);
    }
}
