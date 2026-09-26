using System.Globalization;
using Level5.Domain.Results;

namespace Level5.Application.Migration;

/// <summary>
/// Plain, primitive-typed shape of one V1 <c>highscores</c> row - deliberately not the migration
/// tool's own raw-reader record, so this stays reachable from Application without pulling in
/// Npgsql or the tool's assembly. Field names mirror V1's column names, not V2's.
/// </summary>
public sealed record LegacyHighscoreMappingInput(
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
    int SniperEnabled);

/// <summary>Every reason <see cref="LegacyHighscoreMapper.TryMap"/> can refuse to map a row. Order matches the checks' own evaluation order.</summary>
public enum LegacyHighscoreMappingBlocker
{
    None,
    MissingOrMalformedScoreid,
    NonPositiveMode,
    NonPositiveLevel,
    // Structurally unreachable for an Int32-typed Characterid (max 11 characters, well within
    // MatchResultFieldLimits.CharacterIdMaxLength) - kept as its own category because the issue
    // explicitly calls out auditing this dimension, and a future narrower limit could make it live.
    CharacterIdNotRepresentable,
    InvalidVersion,
    InvalidPlatform,
    InvalidMetricValue
}

public sealed record LegacyHighscoreMappingResult(
    bool IsValid,
    LegacyHighscoreMappingBlocker Blocker,
    string? BlockDetail,
    Guid ClientResultId,
    int ModeId,
    int LevelId,
    string? CharacterId,
    string? ClientVersion,
    string? Platform,
    MatchResultMetrics? Metrics,
    MatchResultModifiers? Modifiers)
{
    public static LegacyHighscoreMappingResult Valid(
        Guid clientResultId, int modeId, int levelId, string characterId, string clientVersion, string platform,
        MatchResultMetrics metrics, MatchResultModifiers modifiers) =>
        new(true, LegacyHighscoreMappingBlocker.None, null, clientResultId, modeId, levelId, characterId, clientVersion, platform, metrics, modifiers);

    public static LegacyHighscoreMappingResult Invalid(LegacyHighscoreMappingBlocker blocker, string detail) =>
        new(false, blocker, detail, Guid.Empty, 0, 0, null, null, null, null, null);
}

/// <summary>
/// The single canonical V1 <c>highscores</c> -&gt; V2 <see cref="MatchResult"/> field mapping,
/// mirroring Unity's <c>BackendV2MatchResultAdapter</c> exactly (field-for-field, same six metrics,
/// same four <c>!= 0</c> modifier checks, same <c>Guid.TryParse(Scoreid)</c> contract - never a
/// second, independently-generated <c>ClientResultId</c>). Used identically by <c>audit</c> (to
/// classify a row without writing anything), <c>migrate</c> (to build the payload it submits), and
/// <c>verify</c> (to re-derive what a row's <see cref="Level5.Domain.Results.MatchResult"/> should
/// look like and compare it against what was actually persisted) - so the three commands can never
/// silently disagree about what a row means.
///
/// Field-length/positivity checks read from <see cref="MatchResultFieldLimits"/> and
/// <see cref="MatchResultMetrics.Of"/> - the same constants and validation
/// <see cref="MatchResult.Submit"/> itself uses - rather than re-deriving a second interpretation
/// of what a "valid" field looks like. <see cref="MatchResult.Submit"/> remains the final,
/// authoritative gate at the moment a row is actually persisted; this mapper's own checks exist so
/// a blocked row can be classified with a specific, actionable reason before that point, using a
/// throwaway domain construction is deliberately avoided since neither <c>PlayerId</c> nor a
/// persistable timestamp exist yet at classification time.
/// </summary>
public static class LegacyHighscoreMapper
{
    public static LegacyHighscoreMappingResult TryMap(LegacyHighscoreMappingInput input)
    {
        if (!Guid.TryParse(input.Scoreid, out var clientResultId))
        {
            return LegacyHighscoreMappingResult.Invalid(
                LegacyHighscoreMappingBlocker.MissingOrMalformedScoreid,
                $"Scoreid '{input.Scoreid}' is missing or not a valid GUID for mode {input.Modeid} / level {input.Levelid}.");
        }

        if (input.Modeid <= 0)
        {
            return LegacyHighscoreMappingResult.Invalid(
                LegacyHighscoreMappingBlocker.NonPositiveMode, $"Modeid {input.Modeid} is not a positive integer.");
        }

        if (input.Levelid <= 0)
        {
            return LegacyHighscoreMappingResult.Invalid(
                LegacyHighscoreMappingBlocker.NonPositiveLevel, $"Levelid {input.Levelid} is not a positive integer.");
        }

        var characterId = input.Characterid.ToString(CultureInfo.InvariantCulture);
        if (characterId.Length > MatchResultFieldLimits.CharacterIdMaxLength)
        {
            return LegacyHighscoreMappingResult.Invalid(
                LegacyHighscoreMappingBlocker.CharacterIdNotRepresentable,
                $"Characterid {input.Characterid} cannot be represented within {MatchResultFieldLimits.CharacterIdMaxLength} characters.");
        }

        if (!IsWithinLength(input.Version, MatchResultFieldLimits.ClientVersionMaxLength))
        {
            return LegacyHighscoreMappingResult.Invalid(
                LegacyHighscoreMappingBlocker.InvalidVersion,
                $"Version '{input.Version}' is empty or exceeds {MatchResultFieldLimits.ClientVersionMaxLength} characters.");
        }

        if (!IsWithinLength(input.Platform, MatchResultFieldLimits.PlatformMaxLength))
        {
            return LegacyHighscoreMappingResult.Invalid(
                LegacyHighscoreMappingBlocker.InvalidPlatform,
                $"Platform '{input.Platform}' is empty or exceeds {MatchResultFieldLimits.PlatformMaxLength} characters.");
        }

        MatchResultMetrics metrics;
        try
        {
            metrics = MatchResultMetrics.Of(new Dictionary<MatchResultMetric, double>
            {
                [MatchResultMetric.TotalPoints] = input.TotalPoints,
                [MatchResultMetric.ShotsMade] = input.MaxShotMade,
                [MatchResultMetric.TotalDistance] = input.TotalDistance,
                [MatchResultMetric.CompletionTimeSeconds] = input.Time,
                [MatchResultMetric.LongestStreak] = input.ConsecutiveShots,
                [MatchResultMetric.EnemiesKilled] = input.EnemiesKilled,
            });
        }
        catch (InvalidMatchResultException ex)
        {
            return LegacyHighscoreMappingResult.Invalid(LegacyHighscoreMappingBlocker.InvalidMetricValue, ex.Message);
        }

        var modifiers = MatchResultModifiers.Of(
            hardcore: input.HardcoreEnabled != 0,
            trafficEnabled: input.TrafficEnabled != 0,
            enemiesEnabled: input.EnemiesEnabled != 0,
            sniperEnabled: input.SniperEnabled != 0);

        return LegacyHighscoreMappingResult.Valid(
            clientResultId, input.Modeid, input.Levelid, characterId, input.Version!, input.Platform!, metrics, modifiers);
    }

    private static bool IsWithinLength(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;
}
