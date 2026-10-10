using Level5.Infrastructure.BloodMoney;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class BloodMoneyChatConstraintTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migrated_schema_has_canonical_composite_FKs_restrictive_deletes_and_only_bounded_message_indexes()
    {
        await using var db = fixture.CreateDbContext();
        var constraints = await db.Database.SqlQueryRaw<Constraint>("""
            SELECT conname AS "Name", contype::text AS "Type", confdeltype::text AS "DeleteAction", pg_get_constraintdef(oid) AS "Definition"
            FROM pg_constraint WHERE conrelid IN ('blood_money_chat_messages'::regclass, 'blood_money_chat_participant_state'::regclass)
            """).ToListAsync();
        Assert.Equal(2, constraints.Count(c => c.Type == "p"));
        Assert.Equal(2, constraints.Count(c => c.Type == "f"));
        Assert.All(constraints.Where(c => c.Type == "f"), c =>
        {
            Assert.Equal("r", c.DeleteAction);
            Assert.Contains("REFERENCES blood_money_challenge_participants(\"ChallengeId\", \"PlayerId\")", c.Definition);
        });
        var indexes = await db.Database.SqlQueryRaw<string>("""
            SELECT indexdef AS "Value" FROM pg_indexes WHERE tablename = 'blood_money_chat_messages'
            """).ToListAsync();
        Assert.Equal(4, indexes.Count);
        Assert.Contains(indexes, i => i.Contains("UNIQUE") && i.Contains("\"ChallengeId\", \"Sequence\""));
        Assert.Contains(indexes, i => i.Contains("UNIQUE") && i.Contains("\"ChallengeId\", \"SenderPlayerId\", \"ClientMessageId\""));
        Assert.Contains(indexes, i => i.Contains("\"ChallengeId\", \"SenderPlayerId\", \"CreatedAt\", \"Sequence\""));
        Assert.DoesNotContain(indexes, i => i.Contains("Body"));
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Theory]
    [InlineData("empty-id")][InlineData("empty-key")][InlineData("zero-sequence")][InlineData("negative-sequence")]
    [InlineData("visibility")][InlineData("empty-body")][InlineData("2001-bytes")]
    [InlineData("sender-fk")][InlineData("challenge-fk")]
    public async Task PostgreSQL_rejects_invalid_message_rows(string invalid)
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed();
        await using var db = fixture.CreateDbContext();
        var row = Message(id.Value, players[0].Value);
        switch (invalid)
        {
            case "empty-id": row.Id = Guid.Empty; break;
            case "empty-key": row.ClientMessageId = Guid.Empty; break;
            case "zero-sequence": row.Sequence = 0; break;
            case "negative-sequence": row.Sequence = -1; break;
            case "visibility": row.Visibility = "Unknown"; break;
            case "empty-body": row.Body = ""; break;
            case "2001-bytes": row.Body = string.Concat(Enumerable.Repeat("😀", 500)) + "a"; break;
            case "sender-fk":
                // A real shared player from another challenge still isn't in this canonical roster.
                row.SenderPlayerId = (await new BloodMoneyChatTests(fixture).Seed()).Players[0].Value; break;
            case "challenge-fk": row.ChallengeId = Guid.NewGuid(); break;
        }
        db.Add(row);
        var error = Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException);
        Assert.Equal(invalid.EndsWith("-fk", StringComparison.Ordinal) ? PostgresErrorCodes.ForeignKeyViolation : PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Theory]
    [InlineData("primary")][InlineData("sequence")][InlineData("key")]
    public async Task PostgreSQL_defends_message_identity_sequence_and_sender_scoped_retry_uniqueness(string duplicate)
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed();
        await using var db = fixture.CreateDbContext(); var first = Message(id.Value, players[0].Value);
        db.Add(first); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var second = Message(id.Value, players[0].Value); second.Sequence = 2;
        if (duplicate == "primary") second.Id = first.Id;
        if (duplicate == "sequence") second.Sequence = first.Sequence;
        if (duplicate == "key") second.ClientMessageId = first.ClientMessageId;
        db.Add(second);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqlState);
    }

    [Fact]
    public async Task PostgreSQL_accepts_2000_byte_body_and_supported_visibility_but_restricts_participant_deletion()
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed();
        await using var db = fixture.CreateDbContext(); var row = Message(id.Value, players[0].Value);
        row.Body = string.Concat(Enumerable.Repeat("😀", 500)); row.Visibility = "Suppressed";
        db.Add(row); await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM blood_money_challenge_participants WHERE "ChallengeId" = {id.Value} AND "PlayerId" = {players[0].Value}
            """));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, error.SqlState);
    }

    [Theory]
    [InlineData("negative-read")][InlineData("player-fk")][InlineData("challenge-fk")][InlineData("duplicate")]
    public async Task PostgreSQL_defends_private_state_integrity(string invalid)
    {
        var (id, players) = await new BloodMoneyChatTests(fixture).Seed();
        await using var db = fixture.CreateDbContext();
        var row = new BloodMoneyChatParticipantStateRow { ChallengeId = id.Value, PlayerId = players[0].Value };
        if (invalid == "negative-read") row.LastReadSequence = -1;
        if (invalid == "player-fk") row.PlayerId = Guid.NewGuid();
        if (invalid == "challenge-fk") row.ChallengeId = Guid.NewGuid();
        if (invalid == "duplicate")
        {
            db.Add(new BloodMoneyChatParticipantStateRow { ChallengeId = row.ChallengeId, PlayerId = row.PlayerId });
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        }
        db.Add(row);
        var error = Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException);
        Assert.Equal(invalid == "duplicate" ? PostgresErrorCodes.UniqueViolation : invalid == "negative-read" ? PostgresErrorCodes.CheckViolation : PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    private static BloodMoneyChatMessageRow Message(Guid challengeId, Guid actor) => new()
    {
        Id = Guid.NewGuid(), ChallengeId = challengeId, Sequence = 1, SenderPlayerId = actor, ClientMessageId = Guid.NewGuid(),
        Body = "hello", CreatedAt = BloodMoneyChatTests.Start, Visibility = "Visible"
    };
    private sealed class Constraint
    {
        public string Name { get; set; } = null!;
        public string Type { get; set; } = null!;
        public string DeleteAction { get; set; } = null!;
        public string Definition { get; set; } = null!;
    }
}
