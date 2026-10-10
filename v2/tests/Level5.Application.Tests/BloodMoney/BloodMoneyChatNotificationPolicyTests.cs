using Level5.Application.BloodMoney;
using Level5.Domain.BloodMoney;
using Xunit;

namespace Level5.Application.Tests.BloodMoney;

public sealed class BloodMoneyChatNotificationPolicyTests
{
    [Fact]
    public void Keys_are_utc_versioned_challenge_scoped_and_change_at_the_exact_boundary()
    {
        var policy = new BloodMoneyChatNotificationPolicy("v1", TimeSpan.FromMinutes(5));
        var id = new BloodMoneyChallengeId(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var start = DateTimeOffset.FromUnixTimeSeconds(300);
        var key = policy.SourceEventKey(id, start);
        Assert.Equal("chat-v1:11111111222233334444555555555555:300", key);
        Assert.Equal(key, policy.SourceEventKey(id, start.AddMinutes(5).AddTicks(-1)));
        Assert.Equal(key, policy.SourceEventKey(id, start.ToOffset(TimeSpan.FromHours(-5))));
        Assert.NotEqual(key, policy.SourceEventKey(id, start.AddMinutes(5)));
        Assert.NotEqual(key, policy.SourceEventKey(id, start.AddTicks(-1)));
        Assert.NotEqual(key, policy.SourceEventKey(new(Guid.NewGuid()), start));
        Assert.NotEqual(key, new BloodMoneyChatNotificationPolicy("v2", policy.Window).SourceEventKey(id, start));
        Assert.EndsWith(":-300", policy.SourceEventKey(id, DateTimeOffset.UnixEpoch.AddTicks(-1)));
        Assert.True(new BloodMoneyChatNotificationPolicy(new string('v', 32), TimeSpan.FromSeconds(1))
            .SourceEventKey(id, DateTimeOffset.MinValue).Length <= 128);
    }

    [Theory]
    [InlineData(null, 3000000000L)][InlineData("", 3000000000L)][InlineData(" ", 3000000000L)]
    [InlineData("v:1", 3000000000L)][InlineData("v1", 0L)][InlineData("v1", -10000000L)]
    [InlineData("v1", 1L)][InlineData("v1", 10000001L)]
    [InlineData("123456789012345678901234567890123", 3000000000L)]
    public void Invalid_policies_are_rejected(string? version, long ticks)
        => Assert.Throws<ArgumentException>(() => new BloodMoneyChatNotificationPolicy(version!, TimeSpan.FromTicks(ticks)));
}
