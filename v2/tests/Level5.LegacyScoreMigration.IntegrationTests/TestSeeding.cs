using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Rows;

namespace Level5.LegacyScoreMigration.IntegrationTests;

/// <summary>Shared V2 seeding helpers used across the audit/migrate/verify/use-case test files.</summary>
internal static class TestSeeding
{
    public static async Task<(Guid AccountId, Guid PlayerId)> SeedLinkedAccountAsync(Level5V2DbContext db, int legacyUserId, string prefix)
    {
        var accountId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var unique = Guid.NewGuid().ToString("N")[..12];

        db.Accounts.Add(new AccountRow
        {
            Id = accountId,
            Username = $"{prefix}{unique}",
            UsernameCanonical = $"{prefix}{unique}".ToUpperInvariant(),
            Status = "Active",
            PasswordHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.PlayerProfiles.Add(new PlayerProfileRow
        {
            Id = playerId,
            AccountId = accountId,
            DisplayName = $"Player{unique}",
            Tag = $"T{unique}"[..Math.Min(12, $"T{unique}".Length)],
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.LegacyAccountLinks.Add(new LegacyAccountLinkRow
        {
            LegacyUserId = legacyUserId,
            AccountId = accountId,
            PlayerId = playerId,
            LegacyUsername = $"{prefix}{unique}",
            MigratedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        return (accountId, playerId);
    }

    /// <summary>An Account + PlayerProfile with no legacy_account_links row - a real, valid PlayerId that no legacy user currently resolves to, for corrupting a link's PlayerId without colliding with legacy_account_links' own unique index on PlayerId.</summary>
    public static async Task<Guid> SeedUnlinkedPlayerAsync(Level5V2DbContext db, string prefix)
    {
        var accountId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var unique = Guid.NewGuid().ToString("N")[..12];

        db.Accounts.Add(new AccountRow
        {
            Id = accountId,
            Username = $"{prefix}{unique}",
            UsernameCanonical = $"{prefix}{unique}".ToUpperInvariant(),
            Status = "Active",
            PasswordHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.PlayerProfiles.Add(new PlayerProfileRow
        {
            Id = playerId,
            AccountId = accountId,
            DisplayName = $"Player{unique}",
            Tag = $"U{unique}"[..Math.Min(12, $"U{unique}".Length)],
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        return playerId;
    }
}
