using Level5.Domain.Ids;
using Level5.Domain.Social;
using Xunit;

namespace Level5.Domain.Tests.Social;

public class FriendshipTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Between_canonically_orders_players_regardless_of_call_order()
    {
        var a = PlayerId.New();
        var b = PlayerId.New();

        var ab = Friendship.Between(a, b, Now);
        var ba = Friendship.Between(b, a, Now);

        Assert.Equal(ab.LowerPlayerId, ba.LowerPlayerId);
        Assert.Equal(ab.UpperPlayerId, ba.UpperPlayerId);
    }

    [Fact]
    public void Between_self_is_rejected()
    {
        var a = PlayerId.New();

        Assert.Throws<InvalidOperationException>(() => Friendship.Between(a, a, Now));
    }

    [Fact]
    public void OtherPlayer_returns_the_counterpart()
    {
        var a = PlayerId.New();
        var b = PlayerId.New();
        var friendship = Friendship.Between(a, b, Now);

        Assert.Equal(b, friendship.OtherPlayer(a));
        Assert.Equal(a, friendship.OtherPlayer(b));
    }

    [Fact]
    public void OtherPlayer_for_a_non_participant_throws()
    {
        var friendship = Friendship.Between(PlayerId.New(), PlayerId.New(), Now);

        Assert.Throws<InvalidOperationException>(() => friendship.OtherPlayer(PlayerId.New()));
    }
}
