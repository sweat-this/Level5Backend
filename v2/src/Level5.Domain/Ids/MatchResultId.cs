namespace Level5.Domain.Ids;

/// <summary>Opaque identity for a persisted <see cref="Results.MatchResult"/>.</summary>
public readonly record struct MatchResultId(Guid Value)
{
    public static MatchResultId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
