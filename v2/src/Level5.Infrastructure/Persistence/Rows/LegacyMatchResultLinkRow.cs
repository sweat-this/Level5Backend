namespace Level5.Infrastructure.Persistence.Rows;

/// <summary>
/// Durable record of a V1 <c>highscores.id</c> -&gt; V2 <see cref="MatchResultRow"/> migration.
/// Read-only artifact of the offline legacy score migration tool; nothing else writes it.
/// </summary>
public sealed class LegacyMatchResultLinkRow
{
    public int LegacyHighscoreId { get; set; }
    public Guid MatchResultId { get; set; }
    public string? LegacyScoreId { get; set; }
    public DateTimeOffset MigratedAt { get; set; }
}
