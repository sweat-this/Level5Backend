namespace Level5.Domain.Ids;

public readonly record struct VersusSeriesId(Guid Value)
{
    public static VersusSeriesId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
