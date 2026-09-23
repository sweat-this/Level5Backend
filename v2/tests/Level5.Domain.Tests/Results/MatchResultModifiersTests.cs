using Level5.Domain.Results;
using Xunit;

namespace Level5.Domain.Tests.Results;

public class MatchResultModifiersTests
{
    [Fact]
    public void Equality_compares_every_field()
    {
        var a = MatchResultModifiers.Of(hardcore: true, trafficEnabled: false, enemiesEnabled: true, sniperEnabled: false);
        var b = MatchResultModifiers.Of(hardcore: true, trafficEnabled: false, enemiesEnabled: true, sniperEnabled: false);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void Equality_fails_when_any_single_field_differs(bool hardcore, bool traffic, bool enemies, bool sniper)
    {
        var baseline = MatchResultModifiers.Of(false, false, false, false);
        var changed = MatchResultModifiers.Of(hardcore, traffic, enemies, sniper);

        Assert.NotEqual(baseline, changed);
    }
}
