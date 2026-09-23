using Level5.Application.Common;
using Level5.Application.Results;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Ids;
using Level5.Domain.Results;
using Xunit;

namespace Level5.Application.Tests.Results;

public class SubmitMatchResultUseCaseTests
{
    private readonly InMemoryMatchResultStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly SubmitMatchResultUseCase _submit;

    public SubmitMatchResultUseCaseTests()
    {
        _submit = new SubmitMatchResultUseCase(_store, _clock);
    }

    private static MatchResultMetrics Metrics(double totalPoints = 90) =>
        MatchResultMetrics.Of(new Dictionary<MatchResultMetric, double> { [MatchResultMetric.TotalPoints] = totalPoints });

    private static readonly MatchResultModifiers Modifiers = MatchResultModifiers.Of(false, false, false, false);

    private static SubmitMatchResultRequest Request(
        PlayerId playerId, Guid? clientResultId = null, string modeId = "arcade", string levelId = "level-1",
        string characterId = "hero", string clientVersion = "1.0.0", string platform = "ios",
        MatchResultMetrics? metrics = null, MatchResultModifiers? modifiers = null)
        => new(playerId, clientResultId ?? Guid.NewGuid(), modeId, levelId, characterId, clientVersion, platform,
            metrics ?? Metrics(), modifiers ?? Modifiers);

    [Fact]
    public async Task First_submission_is_persisted()
    {
        var player = PlayerId.New();

        var result = await _submit.ExecuteAsync(Request(player), CancellationToken.None);

        Assert.Equal(player, result.PlayerId);
        var found = await _store.FindByClientResultIdAsync(player, result.ClientResultId, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(result.Id, found.Id);
    }

    [Fact]
    public async Task Identical_replay_returns_the_original_result_without_creating_a_second_row()
    {
        var player = PlayerId.New();
        var clientResultId = Guid.NewGuid();

        var first = await _submit.ExecuteAsync(Request(player, clientResultId), CancellationToken.None);
        var replay = await _submit.ExecuteAsync(Request(player, clientResultId), CancellationToken.None);

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.CreatedAt, replay.CreatedAt);
    }

    [Fact]
    public async Task Replay_with_a_different_metric_value_conflicts()
    {
        var player = PlayerId.New();
        var clientResultId = Guid.NewGuid();
        await _submit.ExecuteAsync(Request(player, clientResultId, metrics: Metrics(90)), CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _submit.ExecuteAsync(Request(player, clientResultId, metrics: Metrics(91)), CancellationToken.None));
    }

    [Theory]
    [InlineData("other-mode", "level-1", "hero", "1.0.0", "ios")]
    [InlineData("arcade", "other-level", "hero", "1.0.0", "ios")]
    [InlineData("arcade", "level-1", "other-hero", "1.0.0", "ios")]
    [InlineData("arcade", "level-1", "hero", "2.0.0", "ios")]
    [InlineData("arcade", "level-1", "hero", "1.0.0", "android")]
    public async Task Replay_with_any_different_client_field_conflicts(
        string modeId, string levelId, string characterId, string clientVersion, string platform)
    {
        var player = PlayerId.New();
        var clientResultId = Guid.NewGuid();
        await _submit.ExecuteAsync(
            Request(player, clientResultId, modeId: "arcade", levelId: "level-1", characterId: "hero", clientVersion: "1.0.0", platform: "ios"),
            CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _submit.ExecuteAsync(
                Request(player, clientResultId, modeId: modeId, levelId: levelId, characterId: characterId, clientVersion: clientVersion, platform: platform),
                CancellationToken.None));
    }

    [Fact]
    public async Task Replay_with_different_modifiers_conflicts()
    {
        var player = PlayerId.New();
        var clientResultId = Guid.NewGuid();
        await _submit.ExecuteAsync(Request(player, clientResultId, modifiers: Modifiers), CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _submit.ExecuteAsync(
                Request(player, clientResultId, modifiers: MatchResultModifiers.Of(true, false, false, false)),
                CancellationToken.None));
    }

    [Fact]
    public async Task The_same_clientResultId_is_allowed_for_different_players()
    {
        var playerA = PlayerId.New();
        var playerB = PlayerId.New();
        var sharedKey = Guid.NewGuid();

        var resultA = await _submit.ExecuteAsync(Request(playerA, sharedKey), CancellationToken.None);
        var resultB = await _submit.ExecuteAsync(Request(playerB, sharedKey), CancellationToken.None);

        Assert.NotEqual(resultA.Id, resultB.Id);
    }

    [Fact]
    public async Task A_conflicting_replay_does_not_alter_the_originally_persisted_result()
    {
        var player = PlayerId.New();
        var clientResultId = Guid.NewGuid();
        var original = await _submit.ExecuteAsync(Request(player, clientResultId, metrics: Metrics(90)), CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _submit.ExecuteAsync(Request(player, clientResultId, metrics: Metrics(999)), CancellationToken.None));

        var stillPersisted = await _store.FindByClientResultIdAsync(player, clientResultId, CancellationToken.None);
        Assert.Equal(original.Metrics, stillPersisted!.Metrics);
    }

    [Fact]
    public async Task Concurrent_identical_submissions_converge_on_the_single_accepted_result()
    {
        // Simulates: both requests' initial lookup misses, then one loses the insert race. The
        // winner commits first (seeded here via the ordinary path); the loser (wired to
        // RacingMatchResultStore, whose own first lookup is forced to miss and whose AddAsync
        // always throws ConflictException) then reloads against the winner's already-committed
        // row and must return that same result rather than surfacing the conflict to the caller.
        var player = PlayerId.New();
        var clientResultId = Guid.NewGuid();
        var winner = await _submit.ExecuteAsync(Request(player, clientResultId, metrics: Metrics(90)), CancellationToken.None);

        var losingSubmit = new SubmitMatchResultUseCase(new RacingMatchResultStore(_store), _clock);
        var loserResult = await losingSubmit.ExecuteAsync(Request(player, clientResultId, metrics: Metrics(90)), CancellationToken.None);

        Assert.Equal(winner.Id, loserResult.Id);
    }

    [Fact]
    public async Task Concurrent_conflicting_submissions_surface_a_conflict_not_last_write_wins()
    {
        var player = PlayerId.New();
        var clientResultId = Guid.NewGuid();
        var winner = await _submit.ExecuteAsync(Request(player, clientResultId, metrics: Metrics(90)), CancellationToken.None);

        var losingSubmit = new SubmitMatchResultUseCase(new RacingMatchResultStore(_store), _clock);

        await Assert.ThrowsAsync<ConflictException>(() =>
            losingSubmit.ExecuteAsync(Request(player, clientResultId, metrics: Metrics(999)), CancellationToken.None));

        var stillPersisted = await _store.FindByClientResultIdAsync(player, clientResultId, CancellationToken.None);
        Assert.Equal(winner.Metrics, stillPersisted!.Metrics);
    }
}
