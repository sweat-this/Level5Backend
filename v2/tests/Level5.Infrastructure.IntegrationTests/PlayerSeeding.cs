using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Domain.Players;
using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;

namespace Level5.Infrastructure.IntegrationTests;

/// <summary>
/// Seeds a real account + player_profiles row. Needed wherever a test constructs a
/// FriendRequest/Friendship/VersusSeries against a player id (issue #20 added database foreign
/// keys from friend_requests/friendships/competitive_series player columns to
/// player_profiles.Id, so a bare PlayerId.New() with no backing row no longer satisfies them).
/// </summary>
internal static class PlayerSeeding
{
    public static async Task<PlayerId> CreatePlayerAsync(Level5V2DbContext db, string label, DateTimeOffset now)
    {
        // Cap the label at a fixed length before appending guid digits, rather than slicing
        // "label + guid" to a fixed total length - that previous approach let a long enough label
        // consume the entire slice and leave no random digits at all, which is exactly what caused
        // an intermittent duplicate-tag collision across test methods sharing one Postgres
        // container before this was fixed. Capping the label first guarantees a constant amount of
        // entropy (24 hex digits for the username, 12 for the tag) no matter how long the caller's
        // label is.
        var prefix = label.Length > 8 ? label[..8] : label;
        var account = Account.Register(Username.Create(prefix + Guid.NewGuid().ToString("N")[..24]), "hash", now);
        await new AccountStore(db).AddAsync(account, CancellationToken.None);

        var tag = PlayerTag.Create(prefix + Guid.NewGuid().ToString("N")[..12] + "#" + Random.Shared.Next(100, 999));
        var profile = PlayerProfile.Create(account.Id, label, tag, now);
        await new PlayerProfileStore(db).AddAsync(profile, CancellationToken.None);

        await db.SaveChangesAsync();
        return profile.Id;
    }
}
