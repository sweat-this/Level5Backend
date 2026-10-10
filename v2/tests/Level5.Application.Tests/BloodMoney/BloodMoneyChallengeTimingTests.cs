using Level5.Application.BloodMoney;
using Level5.Application.Common;
using Xunit;

namespace Level5.Application.Tests.BloodMoney;

public sealed class BloodMoneyChallengeTimingTests
{
    [Theory]
    [InlineData(0, 1)] [InlineData(1, 0)] [InlineData(-1, 1)] [InlineData(1, -1)]
    public void Host_must_supply_positive_acceptance_and_gameplay_windows(int acceptance, int gameplay)
        => Assert.Throws<ValidationFailedException>(() => new BloodMoneyChallengeTimingPolicy(TimeSpan.FromSeconds(acceptance), TimeSpan.FromSeconds(gameplay)));
}
