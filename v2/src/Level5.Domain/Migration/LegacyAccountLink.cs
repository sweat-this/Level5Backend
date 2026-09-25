using Level5.Domain.Ids;

namespace Level5.Domain.Migration;

/// <summary>
/// Durable provenance record: this V1 <c>users.userid</c> was migrated to this V2
/// Account/PlayerProfile pair. Immutable once created - a link is never edited, only created (and
/// never deleted, since a future score-migration slice depends on every link staying resolvable).
/// </summary>
public sealed record LegacyAccountLink(
    int LegacyUserId,
    AccountId AccountId,
    PlayerId PlayerId,
    string LegacyUsername,
    DateTimeOffset MigratedAt);
