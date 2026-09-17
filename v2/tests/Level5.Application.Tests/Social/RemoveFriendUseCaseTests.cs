using Level5.Application.Common;
using Level5.Application.Social;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Domain.Players;
using Level5.Domain.Social;
using Xunit;

namespace Level5.Application.Tests.Social;

public class RemoveFriendUseCaseTests
{
    private readonly InMemoryFriendshipStore _friendships = new();
    private readonly InMemoryPlayerProfileStore _profiles = new();
    private readonly FakeClock _clock = new();
    private readonly RemoveFriendUseCase _remove;

    public RemoveFriendUseCaseTests()
    {
        _remove = new RemoveFriendUseCase(_friendships, new NoOpUnitOfWork());
    }

    private async Task<PlayerId> SeedPlayerAsync(string tag)
    {
        var profile = PlayerProfile.Create(AccountId.New(), tag, PlayerTag.Create($"{tag}#0001"), _clock.UtcNow);
        await _profiles.AddAsync(profile, CancellationToken.None);
        return profile.Id;
    }

    [Fact]
    public async Task Removing_an_existing_friendship_succeeds_and_is_no_longer_visible_to_either_player()
    {
        var a = await SeedPlayerAsync("RemA");
        var b = await SeedPlayerAsync("RemB");
        await _friendships.AddFriendshipAsync(Friendship.Between(a, b, _clock.UtcNow), CancellationToken.None);

        await _remove.ExecuteAsync(new RemoveFriendRequest(a, b), CancellationToken.None);

        Assert.False(await _friendships.AreFriendsAsync(a, b, CancellationToken.None));
    }

    [Fact]
    public async Task Removing_a_friendship_that_does_not_exist_is_not_found()
    {
        var a = await SeedPlayerAsync("RemC");
        var b = await SeedPlayerAsync("RemD");

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _remove.ExecuteAsync(new RemoveFriendRequest(a, b), CancellationToken.None));
    }

    [Fact]
    public async Task Removal_works_regardless_of_which_participant_initiates_it()
    {
        var a = await SeedPlayerAsync("RemE");
        var b = await SeedPlayerAsync("RemF");
        await _friendships.AddFriendshipAsync(Friendship.Between(a, b, _clock.UtcNow), CancellationToken.None);

        await _remove.ExecuteAsync(new RemoveFriendRequest(b, a), CancellationToken.None);

        Assert.False(await _friendships.AreFriendsAsync(a, b, CancellationToken.None));
    }
}
