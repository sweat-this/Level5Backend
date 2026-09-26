namespace Level5.LegacyScoreMigration.Legacy;

/// <summary>
/// Plain shape of one V1 <c>highscores</c> row, read via raw SQL - deliberately NOT
/// <c>Level5Backend.Models.Highscore</c> (this tool must never reference the legacy assembly).
/// Every V1-only telemetry/breakdown column (Os, Device, Ipaddress, difficulty, longest shot,
/// shot-attempt counts, two/three/four/seven-point breakdowns, bonuses/money balls, sniper
/// mode/shots/hits, P1-P4 placement/CPU fields, etc.) is deliberately omitted - none of it is part
/// of the V2 match-result contract this migration targets; it remains available in archived V1
/// data only.
/// </summary>
public sealed record LegacyHighscoreRecord(
    int Id,
    int Userid,
    string? Scoreid,
    int Modeid,
    int Levelid,
    int Characterid,
    string? Version,
    string? Platform,
    int TotalPoints,
    int MaxShotMade,
    float TotalDistance,
    float Time,
    int ConsecutiveShots,
    int EnemiesKilled,
    int HardcoreEnabled,
    int TrafficEnabled,
    int EnemiesEnabled,
    int SniperEnabled,
    string? Date);
