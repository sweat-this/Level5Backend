using Level5.Domain.Ids;
using Level5.Domain.Social;
using Xunit;

namespace Level5.Domain.Tests.Social;

public class FriendRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly PlayerId _sender = PlayerId.New();
    private readonly PlayerId _recipient = PlayerId.New();

    [Fact]
    public void Create_against_self_is_rejected()
    {
        Assert.Throws<InvalidFriendRequestException>(() => FriendRequest.Create(_sender, _sender, Now));
    }

    [Fact]
    public void Accept_by_recipient_produces_a_symmetric_friendship()
    {
        var request = FriendRequest.Create(_sender, _recipient, Now);

        var friendship = request.Accept(_recipient, Now);

        Assert.Equal(FriendRequestStatus.Accepted, request.Status);
        Assert.True(friendship.Involves(_sender));
        Assert.True(friendship.Involves(_recipient));
    }

    [Fact]
    public void Accept_by_sender_is_rejected()
    {
        var request = FriendRequest.Create(_sender, _recipient, Now);

        Assert.Throws<FriendRequestAuthorizationException>(() => request.Accept(_sender, Now));
    }

    [Fact]
    public void Decline_by_sender_is_rejected()
    {
        var request = FriendRequest.Create(_sender, _recipient, Now);

        Assert.Throws<FriendRequestAuthorizationException>(() => request.Decline(_sender, Now));
    }

    [Fact]
    public void Cancel_by_recipient_is_rejected()
    {
        var request = FriendRequest.Create(_sender, _recipient, Now);

        Assert.Throws<FriendRequestAuthorizationException>(() => request.Cancel(_recipient, Now));
    }

    [Fact]
    public void Cancel_by_sender_succeeds()
    {
        var request = FriendRequest.Create(_sender, _recipient, Now);

        request.Cancel(_sender, Now);

        Assert.Equal(FriendRequestStatus.Cancelled, request.Status);
    }

    [Fact]
    public void Accepting_an_already_resolved_request_is_an_illegal_transition()
    {
        var request = FriendRequest.Create(_sender, _recipient, Now);
        request.Decline(_recipient, Now);

        Assert.Throws<IllegalFriendRequestTransitionException>(() => request.Accept(_recipient, Now));
    }
}
