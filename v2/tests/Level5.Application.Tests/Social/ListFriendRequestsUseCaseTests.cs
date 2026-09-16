using Level5.Application.Social;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Domain.Players;
using Xunit;

namespace Level5.Application.Tests.Social;

public class ListFriendRequestsUseCaseTests
{
    private readonly InMemoryFriendshipStore _friendships = new();
    private readonly InMemoryPlayerProfileStore _profiles = new();
    private readonly FakeClock _clock = new();
    private readonly SendFriendRequestUseCase _send;
    private readonly ListIncomingFriendRequestsUseCase _listIncoming;
    private readonly ListOutgoingFriendRequestsUseCase _listOutgoing;

    public ListFriendRequestsUseCaseTests()
    {
        _send = new SendFriendRequestUseCase(_friendships, _profiles, new NoOpUnitOfWork(), _clock);
        _listIncoming = new ListIncomingFriendRequestsUseCase(_friendships);
        _listOutgoing = new ListOutgoingFriendRequestsUseCase(_friendships);
    }

    private async Task<PlayerId> SeedPlayerAsync(string tag)
    {
        var profile = PlayerProfile.Create(AccountId.New(), tag, PlayerTag.Create($"{tag}#0001"), _clock.UtcNow);
        await _profiles.AddAsync(profile, CancellationToken.None);
        return profile.Id;
    }

    [Fact]
    public async Task Incoming_only_returns_pending_requests_where_the_current_player_is_the_recipient()
    {
        var a = await SeedPlayerAsync("ListA");
        var b = await SeedPlayerAsync("ListB");
        var c = await SeedPlayerAsync("ListC");
        var aToB = await _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None);

        var bIncoming = await _listIncoming.ExecuteAsync(b, CancellationToken.None);
        var aIncoming = await _listIncoming.ExecuteAsync(a, CancellationToken.None);
        var cIncoming = await _listIncoming.ExecuteAsync(c, CancellationToken.None);

        var only = Assert.Single(bIncoming);
        Assert.Equal(aToB.Id, only.Id);
        Assert.Empty(aIncoming);
        Assert.Empty(cIncoming);
    }

    [Fact]
    public async Task Outgoing_only_returns_pending_requests_where_the_current_player_is_the_sender()
    {
        var a = await SeedPlayerAsync("ListD");
        var b = await SeedPlayerAsync("ListE");
        var c = await SeedPlayerAsync("ListF");
        var aToB = await _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None);

        var aOutgoing = await _listOutgoing.ExecuteAsync(a, CancellationToken.None);
        var bOutgoing = await _listOutgoing.ExecuteAsync(b, CancellationToken.None);
        var cOutgoing = await _listOutgoing.ExecuteAsync(c, CancellationToken.None);

        var only = Assert.Single(aOutgoing);
        Assert.Equal(aToB.Id, only.Id);
        Assert.Empty(bOutgoing);
        Assert.Empty(cOutgoing);
    }

    [Fact]
    public async Task A_third_player_cannot_see_another_pairs_requests_through_either_list()
    {
        var a = await SeedPlayerAsync("ListG");
        var b = await SeedPlayerAsync("ListH");
        var outsider = await SeedPlayerAsync("ListI");
        await _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None);

        Assert.Empty(await _listIncoming.ExecuteAsync(outsider, CancellationToken.None));
        Assert.Empty(await _listOutgoing.ExecuteAsync(outsider, CancellationToken.None));
    }
}
