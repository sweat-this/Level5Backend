using Level5.Domain.Ids;
using Level5.Domain.Platform;
using Xunit;

namespace Level5.Domain.Tests.Platform;

public sealed class PlayerNotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_builds_an_unread_player_owned_notification()
    {
        var playerId = PlayerId.New();

        var notification = Create(playerId);

        Assert.Equal(playerId, notification.RecipientPlayerId);
        Assert.Equal("platform", notification.Source);
        Assert.Equal("friend-request-received", notification.Kind);
        Assert.Null(notification.ReadAt);
    }

    [Fact]
    public void Read_and_unread_are_idempotent()
    {
        var notification = Create(PlayerId.New());

        notification.SetRead(true, Now.AddMinutes(1));
        notification.SetRead(true, Now.AddMinutes(2));
        Assert.Equal(Now.AddMinutes(1), notification.ReadAt);

        notification.SetRead(false, Now.AddMinutes(3));
        notification.SetRead(false, Now.AddMinutes(4));
        Assert.Null(notification.ReadAt);
    }

    [Theory]
    [InlineData("https://evil.example/account")]
    [InlineData("//evil.example/account")]
    [InlineData("/account\\evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,bad")]
    [InlineData("/accounting")]
    [InlineData("/account/login")]
    [InlineData("/account/%6Cogin?returnTo=/account/friends")]
    [InlineData("/account/register/start")]
    [InlineData("/account/%5C%5Cevil.example")]
    public void Create_rejects_unsafe_or_out_of_scope_actions(string actionPath)
    {
        Assert.Throws<InvalidNotificationException>(() => Create(PlayerId.New(), actionPath));
    }

    private static PlayerNotification Create(PlayerId playerId, string? actionPath = "/account/friends")
        => PlayerNotification.Create(
            playerId,
            "platform",
            "friend-request-received",
            "New friend request",
            "You received a new friend request.",
            actionPath,
            Guid.NewGuid().ToString("N"),
            Now);
}
