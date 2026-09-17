using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

/// <summary>
/// Proves the player-profile foreign keys added by issue #20 at the database level, independent
/// of any application-layer validation - rows are inserted directly against the DbSets (not
/// through FriendshipStore/VersusSeriesStore) so a constraint that application code happens to
/// already guard against would still be caught here if it were ever missing from the schema.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PlayerProfileForeignKeyTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static FriendRequestRow ValidFriendRequestRow(Guid from, Guid to) => new()
    {
        Id = Guid.NewGuid(),
        FromPlayerId = from,
        ToPlayerId = to,
        LowerPlayerId = from,
        UpperPlayerId = to,
        Status = "Pending",
        CreatedAt = Now
    };

    private static VersusSeriesRow ValidSeriesRow(Guid challenger, Guid opponent, Guid? winner) => new()
    {
        Id = Guid.NewGuid(),
        ChallengerId = challenger,
        OpponentId = opponent,
        Status = "PendingAcceptance",
        TotalGames = 1,
        CurrentGameNumber = 1,
        WinnerId = winner,
        CreatedAt = Now,
        UpdatedAt = Now,
        StateJson = "{}"
    };

    [Fact]
    public async Task Friend_request_with_a_nonexistent_FromPlayerId_is_rejected()
    {
        await using var db = fixture.CreateDbContext();
        var to = (await PlayerSeeding.CreatePlayerAsync(db, "To", Now)).Value;

        var row = ValidFriendRequestRow(Guid.NewGuid(), to);
        row.LowerPlayerId = to;
        row.UpperPlayerId = to;
        db.FriendRequests.Add(row);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Friend_request_with_a_nonexistent_ToPlayerId_is_rejected()
    {
        await using var db = fixture.CreateDbContext();
        var from = (await PlayerSeeding.CreatePlayerAsync(db, "From", Now)).Value;

        var row = ValidFriendRequestRow(from, Guid.NewGuid());
        row.LowerPlayerId = from;
        row.UpperPlayerId = from;
        db.FriendRequests.Add(row);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Friend_request_with_a_nonexistent_LowerPlayerId_is_rejected()
    {
        await using var db = fixture.CreateDbContext();
        var from = (await PlayerSeeding.CreatePlayerAsync(db, "From", Now)).Value;
        var to = (await PlayerSeeding.CreatePlayerAsync(db, "To", Now)).Value;

        var row = ValidFriendRequestRow(from, to);
        row.LowerPlayerId = Guid.NewGuid();
        db.FriendRequests.Add(row);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Friend_request_with_a_nonexistent_UpperPlayerId_is_rejected()
    {
        await using var db = fixture.CreateDbContext();
        var from = (await PlayerSeeding.CreatePlayerAsync(db, "From", Now)).Value;
        var to = (await PlayerSeeding.CreatePlayerAsync(db, "To", Now)).Value;

        var row = ValidFriendRequestRow(from, to);
        row.UpperPlayerId = Guid.NewGuid();
        db.FriendRequests.Add(row);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Friendship_with_a_nonexistent_LowerPlayerId_is_rejected()
    {
        await using var db = fixture.CreateDbContext();
        var upper = (await PlayerSeeding.CreatePlayerAsync(db, "Upper", Now)).Value;

        db.Friendships.Add(new FriendshipRow
        {
            Id = Guid.NewGuid(),
            LowerPlayerId = Guid.NewGuid(),
            UpperPlayerId = upper,
            CreatedAt = Now
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Friendship_with_a_nonexistent_UpperPlayerId_is_rejected()
    {
        await using var db = fixture.CreateDbContext();
        var lower = (await PlayerSeeding.CreatePlayerAsync(db, "Lower", Now)).Value;

        db.Friendships.Add(new FriendshipRow
        {
            Id = Guid.NewGuid(),
            LowerPlayerId = lower,
            UpperPlayerId = Guid.NewGuid(),
            CreatedAt = Now
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Competitive_series_with_a_nonexistent_ChallengerId_is_rejected()
    {
        await using var db = fixture.CreateDbContext();
        var opponent = (await PlayerSeeding.CreatePlayerAsync(db, "Opponent", Now)).Value;

        db.VersusSeries.Add(ValidSeriesRow(Guid.NewGuid(), opponent, winner: null));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Competitive_series_with_a_nonexistent_OpponentId_is_rejected()
    {
        await using var db = fixture.CreateDbContext();
        var challenger = (await PlayerSeeding.CreatePlayerAsync(db, "Challenger", Now)).Value;

        db.VersusSeries.Add(ValidSeriesRow(challenger, Guid.NewGuid(), winner: null));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Competitive_series_with_a_nonexistent_non_null_WinnerId_is_rejected()
    {
        await using var db = fixture.CreateDbContext();
        var challenger = (await PlayerSeeding.CreatePlayerAsync(db, "Challenger", Now)).Value;
        var opponent = (await PlayerSeeding.CreatePlayerAsync(db, "Opponent", Now)).Value;

        db.VersusSeries.Add(ValidSeriesRow(challenger, opponent, winner: Guid.NewGuid()));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Competitive_series_with_a_null_WinnerId_is_accepted()
    {
        await using var db = fixture.CreateDbContext();
        var challenger = (await PlayerSeeding.CreatePlayerAsync(db, "Challenger", Now)).Value;
        var opponent = (await PlayerSeeding.CreatePlayerAsync(db, "Opponent", Now)).Value;
        var row = ValidSeriesRow(challenger, opponent, winner: null);

        db.VersusSeries.Add(row);
        await db.SaveChangesAsync();

        var saved = await db.VersusSeries.AsNoTracking().SingleAsync(r => r.Id == row.Id);
        Assert.Null(saved.WinnerId);
    }

    [Fact]
    public async Task Competitive_series_with_a_real_WinnerId_is_accepted()
    {
        await using var db = fixture.CreateDbContext();
        var challenger = (await PlayerSeeding.CreatePlayerAsync(db, "Challenger", Now)).Value;
        var opponent = (await PlayerSeeding.CreatePlayerAsync(db, "Opponent", Now)).Value;
        var row = ValidSeriesRow(challenger, opponent, winner: challenger);

        db.VersusSeries.Add(row);
        await db.SaveChangesAsync();

        var saved = await db.VersusSeries.AsNoTracking().SingleAsync(r => r.Id == row.Id);
        Assert.Equal(challenger, saved.WinnerId);
    }

    [Fact]
    public async Task Deleting_a_player_profile_referenced_by_a_friend_request_is_restricted()
    {
        await using var seedDb = fixture.CreateDbContext();
        var from = (await PlayerSeeding.CreatePlayerAsync(seedDb, "From", Now)).Value;
        var to = (await PlayerSeeding.CreatePlayerAsync(seedDb, "To", Now)).Value;
        seedDb.FriendRequests.Add(ValidFriendRequestRow(from, to));
        await seedDb.SaveChangesAsync();

        // A separate, otherwise-empty context: it never loads the friend_requests row, so EF has
        // no in-memory graph telling it the relationship would be severed, and the delete reaches
        // Postgres to be rejected by the FK itself - exactly what this test is verifying.
        await using var deleteDb = fixture.CreateDbContext();
        var profileRow = await deleteDb.PlayerProfiles.SingleAsync(p => p.Id == from);
        deleteDb.PlayerProfiles.Remove(profileRow);

        await Assert.ThrowsAsync<DbUpdateException>(() => deleteDb.SaveChangesAsync());
    }

    [Fact]
    public async Task Deleting_a_player_profile_referenced_by_a_friendship_is_restricted()
    {
        await using var seedDb = fixture.CreateDbContext();
        var lower = (await PlayerSeeding.CreatePlayerAsync(seedDb, "Lower", Now)).Value;
        var upper = (await PlayerSeeding.CreatePlayerAsync(seedDb, "Upper", Now)).Value;
        seedDb.Friendships.Add(new FriendshipRow { Id = Guid.NewGuid(), LowerPlayerId = lower, UpperPlayerId = upper, CreatedAt = Now });
        await seedDb.SaveChangesAsync();

        await using var deleteDb = fixture.CreateDbContext();
        var profileRow = await deleteDb.PlayerProfiles.SingleAsync(p => p.Id == lower);
        deleteDb.PlayerProfiles.Remove(profileRow);

        await Assert.ThrowsAsync<DbUpdateException>(() => deleteDb.SaveChangesAsync());
    }

    [Fact]
    public async Task Deleting_a_player_profile_referenced_by_a_competitive_series_is_restricted()
    {
        await using var seedDb = fixture.CreateDbContext();
        var challenger = (await PlayerSeeding.CreatePlayerAsync(seedDb, "Challenger", Now)).Value;
        var opponent = (await PlayerSeeding.CreatePlayerAsync(seedDb, "Opponent", Now)).Value;
        seedDb.VersusSeries.Add(ValidSeriesRow(challenger, opponent, winner: null));
        await seedDb.SaveChangesAsync();

        await using var deleteDb = fixture.CreateDbContext();
        var profileRow = await deleteDb.PlayerProfiles.SingleAsync(p => p.Id == challenger);
        deleteDb.PlayerProfiles.Remove(profileRow);

        await Assert.ThrowsAsync<DbUpdateException>(() => deleteDb.SaveChangesAsync());
    }
}
