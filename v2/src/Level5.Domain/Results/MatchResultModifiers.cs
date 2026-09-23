namespace Level5.Domain.Results;

/// <summary>
/// The modifier dimensions currently demonstrated to affect score filtering/ranking for an
/// ordinary match - not the full resolved match configuration. Immutable and value-equal so a
/// replayed submission can be compared field-for-field against what was originally persisted.
/// </summary>
public sealed class MatchResultModifiers : IEquatable<MatchResultModifiers>
{
    public bool Hardcore { get; }
    public bool TrafficEnabled { get; }
    public bool EnemiesEnabled { get; }
    public bool SniperEnabled { get; }

    private MatchResultModifiers(bool hardcore, bool trafficEnabled, bool enemiesEnabled, bool sniperEnabled)
    {
        Hardcore = hardcore;
        TrafficEnabled = trafficEnabled;
        EnemiesEnabled = enemiesEnabled;
        SniperEnabled = sniperEnabled;
    }

    public static MatchResultModifiers Of(bool hardcore, bool trafficEnabled, bool enemiesEnabled, bool sniperEnabled)
        => new(hardcore, trafficEnabled, enemiesEnabled, sniperEnabled);

    public bool Equals(MatchResultModifiers? other) =>
        other is not null
        && Hardcore == other.Hardcore
        && TrafficEnabled == other.TrafficEnabled
        && EnemiesEnabled == other.EnemiesEnabled
        && SniperEnabled == other.SniperEnabled;

    public override bool Equals(object? obj) => Equals(obj as MatchResultModifiers);

    public override int GetHashCode() => HashCode.Combine(Hardcore, TrafficEnabled, EnemiesEnabled, SniperEnabled);
}
