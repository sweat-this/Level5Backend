using Level5.Application.Abstractions;
using Level5.Application.Leaderboards;
using Level5.Application.Migration;
using Level5.Domain.Ids;
using Level5.Infrastructure.Leaderboards;
using Level5.Infrastructure.Persistence.Repositories;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Level5.LegacyScoreMigration.IntegrationTests;

[Collection(V2PostgresCollection.Name)]
public sealed class ImportLegacyMatchResultUseCaseTests(V2PostgresFixture v2Fixture)
{
    [Fact]
    public async Task A_clean_historical_result_imports()
    {
        var (legacyUserId, playerId) = await SeedLinkedPlayerAsync();
        var scoreid = Guid.NewGuid().ToString("N");

        var result = await ExecuteAsync(new ImportLegacyMatchResultRequest(
            LegacyHighscoreId: NextLegacyHighscoreId(), legacyUserId, ValidSourceRow(scoreid)));

        Assert.Equal(ImportLegacyMatchResultOutcome.Imported, result.Outcome);
        Assert.NotNull(result.MatchResultId);

        await using var db = v2Fixture.CreateDbContext();
        var row = await db.MatchResults.SingleAsync(r => r.Id == result.MatchResultId!.Value.Value);
        Assert.Equal(playerId.Value, row.PlayerId);
        Assert.Equal(Guid.Parse(scoreid), row.ClientResultId);
    }

    [Fact]
    public async Task Missing_legacy_account_link_blocks()
    {
        var result = await ExecuteAsync(new ImportLegacyMatchResultRequest(
            NextLegacyHighscoreId(), LegacyUserId: 999_999, ValidSourceRow(Guid.NewGuid().ToString("N"))));

        Assert.Equal(ImportLegacyMatchResultOutcome.Blocked, result.Outcome);
        Assert.Contains("No legacy_account_links row", result.BlockReason);
    }

    [Fact]
    public async Task An_existing_identical_v2_result_is_reused_not_duplicated()
    {
        var (legacyUserId, playerId) = await SeedLinkedPlayerAsync();
        var scoreid = Guid.NewGuid().ToString("N");
        var sourceRow = ValidSourceRow(scoreid);

        // A transition-period match already delivered by a live V2 submission before migration ran.
        await using (var db = v2Fixture.CreateDbContext())
        {
            var store = new MatchResultStore(db);
            var submitted = Level5.Domain.Results.MatchResult.Submit(
                playerId, Guid.Parse(scoreid), sourceRow.Modeid, sourceRow.Levelid, sourceRow.Characterid.ToString(),
                sourceRow.Version!, sourceRow.Platform!,
                Level5.Domain.Results.MatchResultMetrics.Of(new Dictionary<Level5.Domain.Results.MatchResultMetric, double>
                {
                    [Level5.Domain.Results.MatchResultMetric.TotalPoints] = sourceRow.TotalPoints,
                    [Level5.Domain.Results.MatchResultMetric.ShotsMade] = sourceRow.MaxShotMade,
                    [Level5.Domain.Results.MatchResultMetric.TotalDistance] = sourceRow.TotalDistance,
                    [Level5.Domain.Results.MatchResultMetric.CompletionTimeSeconds] = sourceRow.Time,
                    [Level5.Domain.Results.MatchResultMetric.LongestStreak] = sourceRow.ConsecutiveShots,
                    [Level5.Domain.Results.MatchResultMetric.EnemiesKilled] = sourceRow.EnemiesKilled,
                }),
                Level5.Domain.Results.MatchResultModifiers.Of(false, false, false, false),
                DateTimeOffset.UtcNow);
            await store.AddAsync(submitted, CancellationToken.None);
        }

        var result = await ExecuteAsync(new ImportLegacyMatchResultRequest(NextLegacyHighscoreId(), legacyUserId, sourceRow));

        Assert.Equal(ImportLegacyMatchResultOutcome.AttachedToExistingCompatibleResult, result.Outcome);

        await using var afterDb = v2Fixture.CreateDbContext();
        Assert.Equal(1, await afterDb.MatchResults.CountAsync(r => r.PlayerId == playerId.Value && r.ClientResultId == Guid.Parse(scoreid)));
    }

    [Fact]
    public async Task An_existing_conflicting_v2_result_blocks()
    {
        var (legacyUserId, playerId) = await SeedLinkedPlayerAsync();
        var scoreid = Guid.NewGuid().ToString("N");

        await using (var db = v2Fixture.CreateDbContext())
        {
            var store = new MatchResultStore(db);
            var submitted = Level5.Domain.Results.MatchResult.Submit(
                playerId, Guid.Parse(scoreid), 1, 1, "1", "9.9.9", "Desktop",
                Level5.Domain.Results.MatchResultMetrics.Of(new Dictionary<Level5.Domain.Results.MatchResultMetric, double> { [Level5.Domain.Results.MatchResultMetric.TotalPoints] = 999 }),
                Level5.Domain.Results.MatchResultModifiers.Of(false, false, false, false),
                DateTimeOffset.UtcNow);
            await store.AddAsync(submitted, CancellationToken.None);
        }

        var result = await ExecuteAsync(new ImportLegacyMatchResultRequest(NextLegacyHighscoreId(), legacyUserId, ValidSourceRow(scoreid)));

        Assert.Equal(ImportLegacyMatchResultOutcome.Blocked, result.Outcome);
        Assert.Contains("different material fields", result.BlockReason);

        await using var afterDb = v2Fixture.CreateDbContext();
        Assert.Equal(1, await afterDb.MatchResults.CountAsync(r => r.PlayerId == playerId.Value && r.ClientResultId == Guid.Parse(scoreid)));
    }

    [Fact]
    public async Task A_new_result_and_its_provenance_link_commit_atomically()
    {
        var (legacyUserId, playerId) = await SeedLinkedPlayerAsync();
        var scoreid = Guid.NewGuid().ToString("N");

        // Seed an existing result under (playerId, scoreid) directly, then attempt
        // LegacyMatchResultLinkStore.ImportAsync with a brand-new MatchResult carrying the SAME
        // (playerId, clientResultId) pair - this must fail the unique index inside the store's
        // single SaveChanges call. If the two writes were not atomic, the provenance link could
        // commit even though its MatchResult never did (or vice versa).
        Guid conflictingClientResultId = Guid.Parse(scoreid);
        await using (var seedDb = v2Fixture.CreateDbContext())
        {
            var seeded = Level5.Domain.Results.MatchResult.Submit(
                playerId, conflictingClientResultId, 1, 1, "1", "1.0.0", "Desktop",
                Level5.Domain.Results.MatchResultMetrics.Of(new Dictionary<Level5.Domain.Results.MatchResultMetric, double> { [Level5.Domain.Results.MatchResultMetric.TotalPoints] = 1 }),
                Level5.Domain.Results.MatchResultModifiers.Of(false, false, false, false),
                DateTimeOffset.UtcNow);
            await new MatchResultStore(seedDb).AddAsync(seeded, CancellationToken.None);
        }

        var doomedLegacyHighscoreId = NextLegacyHighscoreId();
        await using (var attemptDb = v2Fixture.CreateDbContext())
        {
            var store = new LegacyMatchResultLinkStore(attemptDb);
            var doomedResult = Level5.Domain.Results.MatchResult.Submit(
                playerId, conflictingClientResultId, 1, 1, "1", "1.0.0", "Desktop",
                Level5.Domain.Results.MatchResultMetrics.Of(new Dictionary<Level5.Domain.Results.MatchResultMetric, double> { [Level5.Domain.Results.MatchResultMetric.TotalPoints] = 2 }),
                Level5.Domain.Results.MatchResultModifiers.Of(false, false, false, false),
                DateTimeOffset.UtcNow);
            var doomedLink = new Level5.Domain.Migration.LegacyMatchResultLink(doomedLegacyHighscoreId, doomedResult.Id, scoreid, DateTimeOffset.UtcNow);

            await Assert.ThrowsAsync<Level5.Application.Common.ConflictException>(
                () => store.ImportAsync(doomedResult, doomedLink, CancellationToken.None));
        }

        await using var db = v2Fixture.CreateDbContext();
        Assert.Equal(1, await db.MatchResults.CountAsync(r => r.PlayerId == playerId.Value && r.ClientResultId == conflictingClientResultId));
        Assert.Equal(0, await db.LegacyMatchResultLinks.CountAsync(l => l.LegacyHighscoreId == doomedLegacyHighscoreId));
    }

    [Fact]
    public async Task A_provenance_link_to_an_existing_compatible_result_works_without_a_second_match_result()
    {
        // Two distinct legacy_highscore_ids can never legitimately share a scoreid in real V1 data
        // (V1 enforces a unique index on highscores.scoreid) - the realistic "attach, don't
        // duplicate" case is a single legacy row whose scoreid was already delivered to V2 by a
        // live, transition-period submission, exactly like a dual-write race.
        var (legacyUserId, playerId) = await SeedLinkedPlayerAsync();
        var scoreid = Guid.NewGuid().ToString("N");
        var sourceRow = ValidSourceRow(scoreid);

        Level5.Domain.Ids.MatchResultId liveSubmittedId;
        await using (var seedDb = v2Fixture.CreateDbContext())
        {
            var submitted = Level5.Domain.Results.MatchResult.Submit(
                playerId, Guid.Parse(scoreid), sourceRow.Modeid, sourceRow.Levelid, sourceRow.Characterid.ToString(),
                sourceRow.Version!, sourceRow.Platform!,
                Level5.Domain.Results.MatchResultMetrics.Of(new Dictionary<Level5.Domain.Results.MatchResultMetric, double>
                {
                    [Level5.Domain.Results.MatchResultMetric.TotalPoints] = sourceRow.TotalPoints,
                    [Level5.Domain.Results.MatchResultMetric.ShotsMade] = sourceRow.MaxShotMade,
                    [Level5.Domain.Results.MatchResultMetric.TotalDistance] = sourceRow.TotalDistance,
                    [Level5.Domain.Results.MatchResultMetric.CompletionTimeSeconds] = sourceRow.Time,
                    [Level5.Domain.Results.MatchResultMetric.LongestStreak] = sourceRow.ConsecutiveShots,
                    [Level5.Domain.Results.MatchResultMetric.EnemiesKilled] = sourceRow.EnemiesKilled,
                }),
                Level5.Domain.Results.MatchResultModifiers.Of(false, false, false, false),
                DateTimeOffset.UtcNow);
            await new MatchResultStore(seedDb).AddAsync(submitted, CancellationToken.None);
            liveSubmittedId = submitted.Id;
        }

        var legacyHighscoreId = NextLegacyHighscoreId();
        var result = await ExecuteAsync(new ImportLegacyMatchResultRequest(legacyHighscoreId, legacyUserId, sourceRow));

        Assert.Equal(ImportLegacyMatchResultOutcome.AttachedToExistingCompatibleResult, result.Outcome);
        Assert.Equal(liveSubmittedId, result.MatchResultId);

        await using var db = v2Fixture.CreateDbContext();
        Assert.Equal(1, await db.MatchResults.CountAsync(r => r.PlayerId == playerId.Value && r.ClientResultId == Guid.Parse(scoreid)));
        var link = await db.LegacyMatchResultLinks.SingleAsync(l => l.LegacyHighscoreId == legacyHighscoreId);
        Assert.Equal(liveSubmittedId.Value, link.MatchResultId);
    }

    [Fact]
    public async Task Rerunning_migration_of_the_same_row_is_idempotent()
    {
        var (legacyUserId, _) = await SeedLinkedPlayerAsync();
        var legacyHighscoreId = NextLegacyHighscoreId();
        var sourceRow = ValidSourceRow(Guid.NewGuid().ToString("N"));

        var first = await ExecuteAsync(new ImportLegacyMatchResultRequest(legacyHighscoreId, legacyUserId, sourceRow));
        var second = await ExecuteAsync(new ImportLegacyMatchResultRequest(legacyHighscoreId, legacyUserId, sourceRow));

        Assert.Equal(ImportLegacyMatchResultOutcome.Imported, first.Outcome);
        Assert.Equal(ImportLegacyMatchResultOutcome.AlreadyLinkedConsistent, second.Outcome);
        Assert.Equal(first.MatchResultId, second.MatchResultId);

        await using var db = v2Fixture.CreateDbContext();
        Assert.Equal(1, await db.LegacyMatchResultLinks.CountAsync(l => l.LegacyHighscoreId == legacyHighscoreId));
    }

    [Fact]
    public async Task Two_concurrent_migrate_attempts_on_the_same_row_never_throw_and_never_duplicate()
    {
        // Real, concurrent execution against the same row (not simulated sequentially): both requests
        // pass ExecuteAsync's own "existing link?" check before either has committed, race to import,
        // and one necessarily loses that race. The loser must resolve gracefully - never surface an
        // unhandled ConflictException - and exactly one MatchResult/link pair must exist afterward.
        var (legacyUserId, playerId) = await SeedLinkedPlayerAsync();
        var legacyHighscoreId = NextLegacyHighscoreId();
        var sourceRow = ValidSourceRow(Guid.NewGuid().ToString("N"));
        var request = new ImportLegacyMatchResultRequest(legacyHighscoreId, legacyUserId, sourceRow);

        var results = await Task.WhenAll(ExecuteAsync(request), ExecuteAsync(request));

        Assert.All(results, r => Assert.NotEqual(ImportLegacyMatchResultOutcome.Blocked, r.Outcome));
        Assert.Contains(results, r => r.Outcome == ImportLegacyMatchResultOutcome.Imported);
        Assert.All(results, r => Assert.Equal(results[0].MatchResultId, r.MatchResultId));

        await using var db = v2Fixture.CreateDbContext();
        Assert.Equal(1, await db.MatchResults.CountAsync(r => r.PlayerId == playerId.Value && r.ClientResultId == Guid.Parse(sourceRow.Scoreid!)));
        Assert.Equal(1, await db.LegacyMatchResultLinks.CountAsync(l => l.LegacyHighscoreId == legacyHighscoreId));
    }

    [Fact]
    public async Task Migrated_higher_wins_leaderboard_rows_rank_correctly()
    {
        var (lowScorerLegacyUserId, lowScorerPlayerId) = await SeedLinkedPlayerAsync();
        var (highScorerLegacyUserId, highScorerPlayerId) = await SeedLinkedPlayerAsync();

        // Mode 1 -> TotalPoints, HigherWins (StaticLeaderboardPolicyCatalog).
        await ExecuteAsync(new ImportLegacyMatchResultRequest(
            NextLegacyHighscoreId(), lowScorerLegacyUserId, ValidSourceRow(Guid.NewGuid().ToString("N"), modeid: 1, totalPoints: 50)));
        await ExecuteAsync(new ImportLegacyMatchResultRequest(
            NextLegacyHighscoreId(), highScorerLegacyUserId, ValidSourceRow(Guid.NewGuid().ToString("N"), modeid: 1, totalPoints: 500)));

        await using var db = v2Fixture.CreateDbContext();
        var page = await new GetLeaderboardUseCase(new StaticLeaderboardPolicyCatalog(), new LeaderboardQuery(db))
            .ExecuteAsync(new GetLeaderboardRequest(1, null, null, null, null, null, null), CancellationToken.None);

        var ranked = page.Items.Where(i => i.Player.PlayerId == lowScorerPlayerId || i.Player.PlayerId == highScorerPlayerId).ToList();
        Assert.Equal(2, ranked.Count);
        Assert.Equal(highScorerPlayerId, ranked[0].Player.PlayerId);
        Assert.Equal(lowScorerPlayerId, ranked[1].Player.PlayerId);
    }

    [Fact]
    public async Task Migrated_lower_wins_time_rows_rank_correctly()
    {
        var (slowLegacyUserId, slowPlayerId) = await SeedLinkedPlayerAsync();
        var (fastLegacyUserId, fastPlayerId) = await SeedLinkedPlayerAsync();

        // Mode 7 -> CompletionTimeSeconds, LowerWins (StaticLeaderboardPolicyCatalog).
        await ExecuteAsync(new ImportLegacyMatchResultRequest(
            NextLegacyHighscoreId(), slowLegacyUserId, ValidSourceRow(Guid.NewGuid().ToString("N"), modeid: 7, time: 90f)));
        await ExecuteAsync(new ImportLegacyMatchResultRequest(
            NextLegacyHighscoreId(), fastLegacyUserId, ValidSourceRow(Guid.NewGuid().ToString("N"), modeid: 7, time: 10f)));

        await using var db = v2Fixture.CreateDbContext();
        var page = await new GetLeaderboardUseCase(new StaticLeaderboardPolicyCatalog(), new LeaderboardQuery(db))
            .ExecuteAsync(new GetLeaderboardRequest(7, null, null, null, null, null, null), CancellationToken.None);

        var ranked = page.Items.Where(i => i.Player.PlayerId == slowPlayerId || i.Player.PlayerId == fastPlayerId).ToList();
        Assert.Equal(2, ranked.Count);
        Assert.Equal(fastPlayerId, ranked[0].Player.PlayerId);
        Assert.Equal(slowPlayerId, ranked[1].Player.PlayerId);
    }

    private async Task<ImportLegacyMatchResultResult> ExecuteAsync(ImportLegacyMatchResultRequest request)
    {
        await using var provider = CommandTestHarness.BuildProvider(v2Fixture.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var useCase = scope.ServiceProvider.GetRequiredService<ImportLegacyMatchResultUseCase>();
        return await useCase.ExecuteAsync(request, CancellationToken.None);
    }

    private static LegacyHighscoreMappingInput ValidSourceRow(
        string scoreid, int modeid = 1, int levelid = 1, int characterid = 5, int totalPoints = 100, float time = 30f) => new(
        scoreid, modeid, levelid, characterid, "1.4.2", "Handheld",
        totalPoints, 8, 42.5f, time, 4, 2, 0, 0, 0, 0);

    private static int _legacyHighscoreIdSeed = 1;
    private static int NextLegacyHighscoreId() => Interlocked.Increment(ref _legacyHighscoreIdSeed);

    private async Task<(int LegacyUserId, PlayerId PlayerId)> SeedLinkedPlayerAsync()
    {
        var legacyUserId = Interlocked.Increment(ref _legacyUserIdSeed);

        await using var db = v2Fixture.CreateDbContext();
        var (_, playerId) = await TestSeeding.SeedLinkedAccountAsync(db, legacyUserId, "scoremig");

        return (legacyUserId, new PlayerId(playerId));
    }

    private static int _legacyUserIdSeed = 1;
}
