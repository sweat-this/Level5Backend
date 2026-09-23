using Level5.Application.Social;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Domain.Players;
using Level5.Domain.Social;
using Xunit;

namespace Level5.Application.Tests.Social;

public class ListFriendsUseCaseTests
{
    private readonly InMemoryFriendshipStore _friendships = new();
    private readonly InMemoryPlayerProfileStore _profiles = new();
    private readonly FakeClock _clock = new();
    private readonly ListFriendsUseCase _useCase;

    public ListFriendsUseCaseTests()
    {
        _useCase = new ListFriendsUseCase(_friendships, _profiles);
    }

    private async Task<PlayerId> SeedPlayerAsync(string tag)
    {
        var profile = PlayerProfile.Create(AccountId.New(), tag, PlayerTag.Create($"{tag}#0001"), _clock.UtcNow);
        await _profiles.AddAsync(profile, CancellationToken.None);
        return profile.Id;
    }

    [Fact]
    public async Task Returns_an_empty_list_when_there_are_no_friends()
    {
        var me = await SeedPlayerAsync("Solo");

        var result = await _useCase.ExecuteAsync(me, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Resolves_the_other_players_profile_for_each_friendship_regardless_of_pair_order()
    {
        var me = await SeedPlayerAsync("Me");
        var friendA = await SeedPlayerAsync("FriendA");
        var friendB = await SeedPlayerAsync("FriendB");
        await _friendships.AddFriendshipAsync(Friendship.Between(me, friendA, _clock.UtcNow), CancellationToken.None);
        await _friendships.AddFriendshipAsync(Friendship.Between(friendB, me, _clock.UtcNow), CancellationToken.None);

        var result = await _useCase.ExecuteAsync(me, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, f => f.PlayerId == friendA);
        Assert.Contains(result, f => f.PlayerId == friendB);
    }

    [Fact]
    public async Task Skips_a_friendship_whose_other_player_profile_no_longer_exists()
    {
        var me = await SeedPlayerAsync("Me2");
        var ghost = PlayerId.New(); // no profile seeded for this id
        await _friendships.AddFriendshipAsync(Friendship.Between(me, ghost, _clock.UtcNow), CancellationToken.None);

        var result = await _useCase.ExecuteAsync(me, CancellationToken.None);

        Assert.Empty(result);
    }
}
