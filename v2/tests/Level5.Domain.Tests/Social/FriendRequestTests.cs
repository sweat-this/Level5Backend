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

    [Fact]
    public void A_newly_created_request_starts_at_revision_zero()
    {
        var request = FriendRequest.Create(_sender, _recipient, Now);

        Assert.Equal(0, request.Revision);
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    [InlineData("cancel")]
    public void A_successful_transition_increments_the_revision_exactly_once(string transition)
    {
        var request = FriendRequest.Create(_sender, _recipient, Now);

        Apply(request, transition, Now);

        Assert.Equal(1, request.Revision);
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    [InlineData("cancel")]
    public void A_rejected_authorization_check_leaves_status_and_revision_unchanged(string transition)
    {
        var request = FriendRequest.Create(_sender, _recipient, Now);
        var stranger = PlayerId.New();

        Assert.Throws<FriendRequestAuthorizationException>(() => Apply(request, transition, Now, stranger));

        Assert.Equal(FriendRequestStatus.Pending, request.Status);
        Assert.Equal(0, request.Revision);
    }

    [Fact]
    public void A_transition_on_an_already_resolved_request_leaves_revision_unchanged()
    {
        var request = FriendRequest.Create(_sender, _recipient, Now);
        request.Decline(_recipient, Now);
        var revisionAfterDecline = request.Revision;

        Assert.Throws<IllegalFriendRequestTransitionException>(() => request.Cancel(_sender, Now));

        Assert.Equal(revisionAfterDecline, request.Revision);
    }

    private void Apply(FriendRequest request, string transition, DateTimeOffset now, PlayerId? actingPlayerId = null)
    {
        switch (transition)
        {
            case "accept":
                request.Accept(actingPlayerId ?? _recipient, now);
                break;
            case "decline":
                request.Decline(actingPlayerId ?? _recipient, now);
                break;
            case "cancel":
                request.Cancel(actingPlayerId ?? _sender, now);
                break;
        }
    }
}
