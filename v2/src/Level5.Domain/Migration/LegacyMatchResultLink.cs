using Level5.Domain.Ids;

namespace Level5.Domain.Migration;

/// <summary>
/// Durable provenance record: this V1 <c>highscores.id</c> was migrated to this V2
/// <see cref="Results.MatchResult"/>. Immutable once created - a link is never edited, only
/// created (and never deleted; <c>match_results</c> rows are restrict-deleted, so a link can never
/// be orphaned). <see cref="LegacyScoreId"/> (V1's <c>scoreid</c>) is debugging/provenance data
/// only, never ownership authority - <see cref="LegacyHighscoreId"/> is the sole migration
/// idempotency key.
/// </summary>
public sealed record LegacyMatchResultLink(
    int LegacyHighscoreId,
    MatchResultId MatchResultId,
    string? LegacyScoreId,
    DateTimeOffset MigratedAt);
