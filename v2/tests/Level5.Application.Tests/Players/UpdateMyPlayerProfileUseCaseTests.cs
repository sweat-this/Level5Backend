using Level5.Application.Common;
using Level5.Application.Players;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Ids;
using Level5.Domain.Players;
using Xunit;

namespace Level5.Application.Tests.Players;

public class UpdateMyPlayerProfileUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly InMemoryPlayerProfileStore _profiles = new();
    private readonly UpdateMyPlayerProfileUseCase _updateMyProfile;

    public UpdateMyPlayerProfileUseCaseTests()
    {
        _updateMyProfile = new UpdateMyPlayerProfileUseCase(_profiles, new NoOpUnitOfWork());
    }

    private async Task<PlayerProfile> SeedProfileAsync(string displayName = "Original")
    {
        var profile = PlayerProfile.Create(AccountId.New(), displayName, PlayerTag.Create("Seed#1234"), Now);
        await _profiles.AddAsync(profile, CancellationToken.None);
        return profile;
    }

    [Fact]
    public async Task Updates_the_authenticated_accounts_own_display_name()
    {
        var profile = await SeedProfileAsync();

        var result = await _updateMyProfile.ExecuteAsync(
            new UpdateMyPlayerProfileRequest(profile.AccountId, "New Name"), CancellationToken.None);

        Assert.Equal("New Name", result.DisplayName);
        Assert.Equal(profile.Id, result.Id);
        Assert.Equal(profile.Tag.Value, result.Tag);
    }

    [Fact]
    public async Task An_invalid_display_name_is_rejected()
    {
        var profile = await SeedProfileAsync();

        await Assert.ThrowsAsync<InvalidDisplayNameException>(() => _updateMyProfile.ExecuteAsync(
            new UpdateMyPlayerProfileRequest(profile.AccountId, "   "), CancellationToken.None));
    }

    [Fact]
    public async Task The_updated_display_name_is_persisted()
    {
        var profile = await SeedProfileAsync();

        await _updateMyProfile.ExecuteAsync(new UpdateMyPlayerProfileRequest(profile.AccountId, "Persisted Name"), CancellationToken.None);

        var reloaded = await _profiles.FindByAccountIdAsync(profile.AccountId, CancellationToken.None);
        Assert.Equal("Persisted Name", reloaded!.DisplayName);
    }

    [Fact]
    public async Task The_returned_public_representation_has_the_new_name_and_the_unchanged_tag()
    {
        var profile = await SeedProfileAsync();

        var result = await _updateMyProfile.ExecuteAsync(
            new UpdateMyPlayerProfileRequest(profile.AccountId, "Renamed"), CancellationToken.None);

        Assert.Equal("Renamed", result.DisplayName);
        Assert.Equal("SEED#1234", result.Tag);
    }

    [Fact]
    public async Task An_account_with_no_profile_yields_a_not_found_application_error()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _updateMyProfile.ExecuteAsync(
            new UpdateMyPlayerProfileRequest(AccountId.New(), "New Name"), CancellationToken.None));
    }
}
