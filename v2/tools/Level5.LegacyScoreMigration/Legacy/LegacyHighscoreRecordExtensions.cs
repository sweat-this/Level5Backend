using Level5.Application.Migration;

namespace Level5.LegacyScoreMigration.Legacy;

/// <summary>Builds the Application-layer mapping input from a raw V1 row - shared by all three commands so they can never disagree about which V1 fields feed the canonical mapping.</summary>
public static class LegacyHighscoreRecordExtensions
{
    public static LegacyHighscoreMappingInput ToMappingInput(this LegacyHighscoreRecord record) => new(
        record.Scoreid, record.Modeid, record.Levelid, record.Characterid, record.Version, record.Platform,
        record.TotalPoints, record.MaxShotMade, record.TotalDistance, record.Time, record.ConsecutiveShots,
        record.EnemiesKilled, record.HardcoreEnabled, record.TrafficEnabled, record.EnemiesEnabled, record.SniperEnabled);
}
