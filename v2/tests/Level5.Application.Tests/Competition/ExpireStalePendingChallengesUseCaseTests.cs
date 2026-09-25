using Level5.Application.Competition;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Competition;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Domain.Players;
using Level5.Domain.Social;
using Xunit;

namespace Level5.Application.Tests.Competition;

public sealed class ExpireStalePendingChallengesUseCaseTests
{
    private readonly InMemoryVersusSeriesStore _series = new();
    private readonly InMemoryFriendshipStore _friendships = new();
    private readonly InMemoryPlayerProfileStore _profiles = new();
    private readonly FakeRulesetCatalog _catalog = new();
    private readonly FakeClock _clock = new();
    private readonly FakeChallengeExpiryPolicy _policy = new();
    private readonly CreateChallengeUseCase _create;
    private readonly AcceptChallengeUseCase _accept;

    public ExpireStalePendingChallengesUseCaseTests()
    {
        _create = new CreateChallengeUseCase(_series, _friendships, _catalog, _clock);
        _accept = new AcceptChallengeUseCase(_series, _clock);
    }

    private async Task<PlayerId> SeedPlayerAsync(string tag)
    {
        var profile = PlayerProfile.Create(AccountId.New(), tag, PlayerTag.Create($"{tag}#0001"), _clock.UtcNow);
        await _profiles.AddAsync(profile, CancellationToken.None);
        return profile.Id;
    }

    private async Task<VersusSeriesId> ChallengeAsync(PlayerId challenger, PlayerId opponent)
    {
        await _friendships.AddFriendshipAsync(Friendship.Between(challenger, opponent, _clock.UtcNow), CancellationToken.None);
        var view = await _create.ExecuteAsync(
            new CreateChallengeRequest(challenger, opponent, "score-only", null, 1, null, Guid.NewGuid()), CancellationToken.None);
        return view.Id;
    }

    private ExpireStalePendingChallengesUseCase MakeUseCase() => new(_series, _clock, _policy);

    [Fact]
    public async Task A_pending_challenge_older_than_the_timeout_is_expired()
    {
        var challenger = await SeedPlayerAsync("StaleChallenger");
        var opponent = await SeedPlayerAsync("StaleOpponent");
        var stale = await ChallengeAsync(challenger, opponent);

        _clock.UtcNow += _policy.PendingAcceptanceTimeout + TimeSpan.FromMinutes(1);

        var expiredCount = await MakeUseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(1, expiredCount);
        var series = await _series.FindByIdAsync(stale, CancellationToken.None);
        Assert.Equal(SeriesStatus.Expired, series!.Status);
        Assert.Equal(_clock.UtcNow, series.CompletedAt);
    }

    [Fact]
    public async Task A_pending_challenge_younger_than_the_timeout_is_left_alone()
    {
        var challenger = await SeedPlayerAsync("FreshChallenger");
        var opponent = await SeedPlayerAsync("FreshOpponent");
        var fresh = await ChallengeAsync(challenger, opponent);

        _clock.UtcNow += _policy.PendingAcceptanceTimeout - TimeSpan.FromMinutes(1);

        var expiredCount = await MakeUseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, expiredCount);
        var series = await _series.FindByIdAsync(fresh, CancellationToken.None);
        Assert.Equal(SeriesStatus.PendingAcceptance, series!.Status);
    }

    [Fact]
    public async Task An_already_accepted_stale_challenge_is_not_expired()
    {
        var challenger = await SeedPlayerAsync("AcceptedChallenger");
        var opponent = await SeedPlayerAsync("AcceptedOpponent");
        var id = await ChallengeAsync(challenger, opponent);
        await _accept.ExecuteAsync(new AcceptChallengeRequest(opponent, id), CancellationToken.None);

        _clock.UtcNow += _policy.PendingAcceptanceTimeout + TimeSpan.FromMinutes(1);

        var expiredCount = await MakeUseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, expiredCount);
        var series = await _series.FindByIdAsync(id, CancellationToken.None);
        Assert.Equal(SeriesStatus.Active, series!.Status);
    }

    [Fact]
    public async Task Re_running_the_sweep_after_a_challenge_already_expired_is_a_no_op()
    {
        var challenger = await SeedPlayerAsync("RepeatChallenger");
        var opponent = await SeedPlayerAsync("RepeatOpponent");
        await ChallengeAsync(challenger, opponent);
        _clock.UtcNow += _policy.PendingAcceptanceTimeout + TimeSpan.FromMinutes(1);
        var useCase = MakeUseCase();
        await useCase.ExecuteAsync(CancellationToken.None);

        var secondRunExpiredCount = await useCase.ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, secondRunExpiredCount);
    }

    [Fact]
    public async Task The_sweep_never_expires_more_than_the_configured_batch_size_in_one_run()
    {
        var challenger = await SeedPlayerAsync("BatchChallenger");
        var opponent = await SeedPlayerAsync("BatchOpponent");
        for (var i = 0; i < 5; i++)
        {
            await ChallengeAsync(challenger, opponent);
        }
        _policy.SweepBatchSize = 3;
        _clock.UtcNow += _policy.PendingAcceptanceTimeout + TimeSpan.FromMinutes(1);

        var expiredCount = await MakeUseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(3, expiredCount);
    }
}
