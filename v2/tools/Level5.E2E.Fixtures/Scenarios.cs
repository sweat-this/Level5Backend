using Level5.Application.Abstractions;
using Level5.Application.Competition;
using Level5.Domain.Competition;
using static Level5.E2E.Fixtures.FixtureIdentities;

namespace Level5.E2E.Fixtures;

/// <summary>
/// The named deterministic scenarios. Every scenario starts from the full 4-account baseline
/// roster (spec section 13) and layers on real use-case calls - nothing here writes to the
/// database directly. Each scenario is independent (a fresh, freshly-migrated database via
/// <c>db.ps1 e2e-reset</c> is assumed before every seed run - see e2e.ps1), so there is no
/// scenario-to-scenario chaining beyond sharing this same baseline-then-layer shape.
/// </summary>
public static class Scenarios
{
    public static readonly IReadOnlyDictionary<string, Func<FixtureContext, CancellationToken, Task>> All = new Dictionary<string, Func<FixtureContext, CancellationToken, Task>>(StringComparer.OrdinalIgnoreCase)
    {
        ["baseline"] = Baseline,
        ["friends"] = Friends,
        ["pending-friend"] = PendingFriend,
        ["challenge-invited"] = ChallengeInvited,
        ["series-active"] = SeriesActiveEntry,
        ["series-one-attempt-complete"] = SeriesOneAttemptComplete,
        ["series-completed"] = SeriesCompleted,
        ["challenge-declined"] = ChallengeDeclined,
        ["challenge-cancelled"] = ChallengeCancelled,
        ["challenge-expired"] = ChallengeExpired,
    };

    /// <summary>4 accounts, 4 profiles, 0 sessions, 0 friend requests, 0 friendships, 0 series.</summary>
    private static Task Baseline(FixtureContext ctx, CancellationToken ct) => ctx.CreateBaselineRosterAsync(ct);

    /// <summary>baseline + Patrick &lt;-&gt; Alice friends.</summary>
    private static async Task Friends(FixtureContext ctx, CancellationToken ct)
    {
        await ctx.CreateBaselineRosterAsync(ct);
        await ctx.BefriendAsync(Patrick, Alice, ct);
    }

    /// <summary>baseline + Patrick -&gt; Bob pending friend request.</summary>
    private static async Task PendingFriend(FixtureContext ctx, CancellationToken ct)
    {
        await ctx.CreateBaselineRosterAsync(ct);
        await ctx.SendFriendRequestAsync(Patrick, Bob, ct);
    }

    /// <summary>Patrick and Alice are friends + Patrick -&gt; Alice invited challenge (PendingAcceptance).</summary>
    private static async Task ChallengeInvited(FixtureContext ctx, CancellationToken ct)
    {
        await ctx.CreateBaselineRosterAsync(ct);
        await ctx.BefriendAsync(Patrick, Alice, ct);
        await ctx.CreateChallengeAsync(Patrick, Alice, ct);
    }

    /// <summary>An accepted Patrick/Alice series: active, known current game, known revision, valid frozen rules.</summary>
    private static async Task<SeriesView> SeriesActive(FixtureContext ctx, CancellationToken ct)
    {
        await ctx.CreateBaselineRosterAsync(ct);
        await ctx.BefriendAsync(Patrick, Alice, ct);
        var created = await ctx.CreateChallengeAsync(Patrick, Alice, ct);

        var acceptChallenge = ctx.Resolve<AcceptChallengeUseCase>();
        var accepted = await acceptChallenge.ExecuteAsync(new AcceptChallengeRequest(ctx.PlayerId(Alice), created.Id), ct);
        ctx.Clock.Advance(TimeSpan.FromSeconds(1));
        return accepted;
    }

    // Explicit non-Task-returning entry point for the dictionary above - SeriesActive is also
    // reused (as a starting point) by SeriesOneAttemptComplete and SeriesCompleted below.
    private static Task SeriesActiveEntry(FixtureContext ctx, CancellationToken ct) => SeriesActive(ctx, ct);

    /// <summary>An active series where Patrick has completed his game-1 attempt and Alice has not - validates sealed-result projection.</summary>
    private static async Task SeriesOneAttemptComplete(FixtureContext ctx, CancellationToken ct)
    {
        var series = await SeriesActive(ctx, ct);

        var startAttempt = ctx.Resolve<StartAttemptUseCase>();
        var completeAttempt = ctx.Resolve<CompleteAttemptUseCase>();

        var descriptor = await startAttempt.ExecuteAsync(new StartAttemptRequest(ctx.PlayerId(Patrick), series.Id, GameNumber: 1), ct);
        ctx.Clock.Advance(TimeSpan.FromSeconds(1));

        await completeAttempt.ExecuteAsync(
            new CompleteAttemptRequest(ctx.PlayerId(Patrick), series.Id, GameNumber: 1, descriptor.AttemptId, MostPointsResult(score: 100, accuracy: 0.9, shotsAttempted: 10)),
            ct);
        ctx.Clock.Advance(TimeSpan.FromSeconds(1));
    }

    /// <summary>A fully resolved best-of-1 series: both participants complete their attempt, the series naturally reaches Completed with a winner and full persisted game/attempt state.</summary>
    private static async Task SeriesCompleted(FixtureContext ctx, CancellationToken ct)
    {
        var series = await SeriesActive(ctx, ct);

        var startAttempt = ctx.Resolve<StartAttemptUseCase>();
        var completeAttempt = ctx.Resolve<CompleteAttemptUseCase>();

        var challengerDescriptor = await startAttempt.ExecuteAsync(new StartAttemptRequest(ctx.PlayerId(Patrick), series.Id, GameNumber: 1), ct);
        ctx.Clock.Advance(TimeSpan.FromSeconds(1));
        await completeAttempt.ExecuteAsync(
            new CompleteAttemptRequest(ctx.PlayerId(Patrick), series.Id, GameNumber: 1, challengerDescriptor.AttemptId, MostPointsResult(score: 100, accuracy: 0.9, shotsAttempted: 10)),
            ct);
        ctx.Clock.Advance(TimeSpan.FromSeconds(1));

        var opponentDescriptor = await startAttempt.ExecuteAsync(new StartAttemptRequest(ctx.PlayerId(Alice), series.Id, GameNumber: 1), ct);
        ctx.Clock.Advance(TimeSpan.FromSeconds(1));
        await completeAttempt.ExecuteAsync(
            new CompleteAttemptRequest(ctx.PlayerId(Alice), series.Id, GameNumber: 1, opponentDescriptor.AttemptId, MostPointsResult(score: 50, accuracy: 0.8, shotsAttempted: 15)),
            ct);
        ctx.Clock.Advance(TimeSpan.FromSeconds(1));
    }

    /// <summary>A Declined terminal record: row retained, terminal_at populated.</summary>
    private static async Task ChallengeDeclined(FixtureContext ctx, CancellationToken ct)
    {
        await ctx.CreateBaselineRosterAsync(ct);
        await ctx.BefriendAsync(Patrick, Alice, ct);
        var created = await ctx.CreateChallengeAsync(Patrick, Alice, ct);

        var decline = ctx.Resolve<DeclineChallengeUseCase>();
        await decline.ExecuteAsync(new DeclineChallengeRequest(ctx.PlayerId(Alice), created.Id), ct);
        ctx.Clock.Advance(TimeSpan.FromSeconds(1));
    }

    /// <summary>A Cancelled terminal record: row retained, terminal_at populated.</summary>
    private static async Task ChallengeCancelled(FixtureContext ctx, CancellationToken ct)
    {
        await ctx.CreateBaselineRosterAsync(ct);
        await ctx.BefriendAsync(Patrick, Alice, ct);
        var created = await ctx.CreateChallengeAsync(Patrick, Alice, ct);

        var cancel = ctx.Resolve<CancelChallengeUseCase>();
        await cancel.ExecuteAsync(new CancelChallengeRequest(ctx.PlayerId(Patrick), created.Id), ct);
        ctx.Clock.Advance(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// An Expired terminal record: row retained, terminal_at populated. Expires the challenge
    /// directly via the same domain transition (<see cref="VersusSeries.Expire"/>) and store save
    /// the real background sweep uses, rather than waiting out the real 30-day timeout - this is
    /// fixture tooling, not a test of the sweep's own timing (that is covered separately by
    /// <c>ExpireStalePendingChallengesUseCase</c>'s own tests).
    /// </summary>
    private static async Task ChallengeExpired(FixtureContext ctx, CancellationToken ct)
    {
        await ctx.CreateBaselineRosterAsync(ct);
        await ctx.BefriendAsync(Patrick, Alice, ct);
        var created = await ctx.CreateChallengeAsync(Patrick, Alice, ct);

        var seriesStore = ctx.Resolve<IVersusSeriesStore>();
        var series = await seriesStore.FindByIdAsync(created.Id, ct) ?? throw new InvalidOperationException("Fixture series not found immediately after creation.");
        var expectedRevision = series.Revision;

        series.Expire(ctx.Clock.UtcNow);
        await seriesStore.TrySaveAsync(series, expectedRevision, ct);
        ctx.Clock.Advance(TimeSpan.FromSeconds(1));
    }

    private static AttemptResult MostPointsResult(double score, double accuracy, double shotsAttempted) => AttemptResult.Of(new Dictionary<ResultMetric, double>
    {
        [ResultMetric.Score] = score,
        [ResultMetric.Accuracy] = accuracy,
        [ResultMetric.ShotsAttempted] = shotsAttempted,
    });
}
