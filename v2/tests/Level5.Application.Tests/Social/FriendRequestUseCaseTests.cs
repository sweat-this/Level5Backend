using Level5.Application.Common;
using Level5.Application.Social;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Domain.Players;
using Xunit;

namespace Level5.Application.Tests.Social;

public class FriendRequestUseCaseTests
{
    private readonly InMemoryFriendshipStore _friendships = new();
    private readonly InMemoryPlayerProfileStore _profiles = new();
    private readonly FakeClock _clock = new();
    private readonly SendFriendRequestUseCase _send;
    private readonly AcceptFriendRequestUseCase _accept;
    private readonly DeclineFriendRequestUseCase _decline;
    private readonly CancelFriendRequestUseCase _cancel;

    public FriendRequestUseCaseTests()
    {
        _send = new SendFriendRequestUseCase(_friendships, _profiles, new NoOpUnitOfWork(), _clock);
        _accept = new AcceptFriendRequestUseCase(_friendships, new NoOpUnitOfWork(), _clock);
        _decline = new DeclineFriendRequestUseCase(_friendships, new NoOpUnitOfWork(), _clock);
        _cancel = new CancelFriendRequestUseCase(_friendships, new NoOpUnitOfWork(), _clock);
    }

    private async Task<PlayerId> SeedPlayerAsync(string tag)
    {
        var profile = PlayerProfile.Create(AccountId.New(), tag, PlayerTag.Create($"{tag}#0001"), _clock.UtcNow);
        await _profiles.AddAsync(profile, CancellationToken.None);
        return profile.Id;
    }

    [Fact]
    public async Task Sending_a_request_to_an_unknown_player_is_rejected()
    {
        var sender = await SeedPlayerAsync("Sender");

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _send.ExecuteAsync(new SendFriendRequestRequest(sender, PlayerId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task Sending_a_duplicate_pending_request_is_rejected()
    {
        var a = await SeedPlayerAsync("Ann");
        var b = await SeedPlayerAsync("Bea");
        await _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None));
    }

    [Fact]
    public async Task Sending_a_reverse_direction_duplicate_pending_request_is_rejected()
    {
        var a = await SeedPlayerAsync("AnnR");
        var b = await SeedPlayerAsync("BeaR");
        await _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _send.ExecuteAsync(new SendFriendRequestRequest(b, a), CancellationToken.None));
    }

    [Fact]
    public async Task Sending_a_request_to_an_existing_friend_is_rejected()
    {
        var a = await SeedPlayerAsync("AnnF");
        var b = await SeedPlayerAsync("BeaF");
        var request = await _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None);
        await _accept.ExecuteAsync(new AcceptFriendRequestRequest(b, request.Id), CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None));
    }

    [Fact]
    public async Task Accepting_creates_a_friendship_visible_to_both_players()
    {
        var a = await SeedPlayerAsync("A2");
        var b = await SeedPlayerAsync("B2");
        var request = await _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None);

        await _accept.ExecuteAsync(new AcceptFriendRequestRequest(b, request.Id), CancellationToken.None);

        Assert.True(await _friendships.AreFriendsAsync(a, b, CancellationToken.None));
    }

    [Fact]
    public async Task Accepting_someone_elses_request_is_rejected()
    {
        var a = await SeedPlayerAsync("A3");
        var b = await SeedPlayerAsync("B3");
        var request = await _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None);

        await Assert.ThrowsAsync<Domain.Social.FriendRequestAuthorizationException>(() =>
            _accept.ExecuteAsync(new AcceptFriendRequestRequest(PlayerId.New(), request.Id), CancellationToken.None));
    }

    [Fact]
    public async Task Declining_someone_elses_request_is_rejected()
    {
        var a = await SeedPlayerAsync("A3d");
        var b = await SeedPlayerAsync("B3d");
        var request = await _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None);

        await Assert.ThrowsAsync<Domain.Social.FriendRequestAuthorizationException>(() =>
            _decline.ExecuteAsync(new DeclineFriendRequestRequest(PlayerId.New(), request.Id), CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_someone_elses_request_is_rejected()
    {
        var a = await SeedPlayerAsync("A3c");
        var b = await SeedPlayerAsync("B3c");
        var request = await _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None);

        await Assert.ThrowsAsync<Domain.Social.FriendRequestAuthorizationException>(() =>
            _cancel.ExecuteAsync(new CancelFriendRequestRequest(PlayerId.New(), request.Id), CancellationToken.None));
    }

    [Fact]
    public async Task After_declining_the_sender_can_send_a_new_request()
    {
        var a = await SeedPlayerAsync("A4");
        var b = await SeedPlayerAsync("B4");
        var request = await _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None);
        await _decline.ExecuteAsync(new DeclineFriendRequestRequest(b, request.Id), CancellationToken.None);

        var second = await _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None);

        Assert.NotEqual(request.Id, second.Id);
    }

    [Fact]
    public async Task Cancelling_lets_a_new_request_be_sent()
    {
        var a = await SeedPlayerAsync("A5");
        var b = await SeedPlayerAsync("B5");
        var request = await _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None);
        await _cancel.ExecuteAsync(new CancelFriendRequestRequest(a, request.Id), CancellationToken.None);

        var second = await _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None);

        Assert.NotEqual(request.Id, second.Id);
    }
}
