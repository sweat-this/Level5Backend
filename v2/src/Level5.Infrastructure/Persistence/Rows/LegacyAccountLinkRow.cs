namespace Level5.Infrastructure.Persistence.Rows;

/// <summary>
/// Durable record of a V1 users.userid -> V2 Account/PlayerProfile migration. Read-only artifact
/// of the offline legacy account migration tool; nothing else writes it. Retained (not just used
/// transiently) so a future score-migration slice can resolve V1 highscores.userid -> V2 PlayerId
/// without re-deriving the mapping.
/// </summary>
public sealed class LegacyAccountLinkRow
{
    public int LegacyUserId { get; set; }
    public Guid AccountId { get; set; }
    public Guid PlayerId { get; set; }
    public required string LegacyUsername { get; set; }
    public DateTimeOffset MigratedAt { get; set; }
}
