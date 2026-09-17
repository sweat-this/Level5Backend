using System.Text.Json;
using Level5.Domain.Competition;
using Level5.Domain.Ids;
using Xunit;

namespace Level5.Domain.Tests.Competition;

/// <summary>
/// Runs the canonical Competition Protocol V1 compatibility fixtures
/// (<c>v2/docs/competition-protocol/fixtures/*.json</c>, see issue #8) against the real
/// <see cref="VersusSeries"/> domain. These fixtures are the single source of truth shared with
/// the Unity <c>level5</c> repository's mirrored EditMode tests - see
/// <c>v2/docs/competition-protocol/README.md</c> for which fixtures are wired to real code on
/// which side. Only fixtures marked <c>"backendExecutable": true</c> are exercised here; the
/// others describe semantics the Backend does not implement yet (tracked as required changes for
/// issues #9-#11) and are asserted here to still be marked non-executable, so a silent flip of
/// that flag without adding a runner is caught rather than ignored.
/// </summary>
public class CompatibilityFixtureTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PlayerId _challenger = PlayerId.New();
    private readonly PlayerId _opponent = PlayerId.New();

    [Theory]
    [InlineData("02-open-target-primary-only.json", false)]
    [InlineData("04-lower-is-better-result.json", false)]
    [InlineData("05-ordered-multi-key-tiebreak.json", false)]
    [InlineData("09-unsupported-ruleset-version.json", false)]
    [InlineData("11-conflicting-result-replay.json", false)]
    [InlineData("01-sealed-attempt-one-completed.json", true)]
    [InlineData("03-higher-is-better-result.json", true)]
    [InlineData("06-true-draw.json", true)]
    [InlineData("07-best-of-3-early-termination.json", true)]
    [InlineData("08-best-of-7-full-run.json", true)]
    [InlineData("10-identical-result-retry.json", true)]
    public void Fixture_manifest_executability_flag_is_unchanged(string fileName, bool expectedBackendExecutable)
    {
        var fixture = Load(fileName);

        Assert.Equal(expectedBackendExecutable, fixture.BackendExecutable);
    }

    [Fact]
    public void Sealed_attempt_one_completed()
    {
        var fixture = Load("01-sealed-attempt-one-completed.json");
        var series = Run(fixture);

        var opponentView = series.ToView(_opponent);
        var round = opponentView.Games.Single(g => g.GameNumber == 1);

        Assert.Equal(AttemptStatus.Completed, round.OpponentAttempt!.Status);
        Assert.Null(round.OpponentAttempt.Score);
        Assert.NotNull(round.YourAttempt);
        Assert.Equal(SeriesStatus.Active, series.Status);
    }

    [Fact]
    public void Higher_is_better_result()
    {
        var fixture = Load("03-higher-is-better-result.json");
        var series = Run(fixture);

        Assert.Equal(SeriesStatus.Completed, series.Status);
        Assert.Equal(_challenger, series.WinnerId);
    }

    [Fact]
    public void True_draw()
    {
        var fixture = Load("06-true-draw.json");
        var series = Run(fixture);

        Assert.Equal(SeriesStatus.Completed, series.Status);
        Assert.Null(series.WinnerId);
    }

    [Fact]
    public void Best_of_3_early_termination()
    {
        var fixture = Load("07-best-of-3-early-termination.json");
        var series = Run(fixture, stopBeforeExpectedRejections: true);

        Assert.Equal(SeriesStatus.Completed, series.Status);
        Assert.Equal(_challenger, series.WinnerId);
        Assert.Equal(2, series.Rounds.Count);
        Assert.Throws<IllegalSeriesTransitionException>(() => series.StartAttempt(_challenger, 3, Now));
    }

    [Fact]
    public void Best_of_7_full_run()
    {
        var fixture = Load("08-best-of-7-full-run.json");
        var series = Run(fixture);

        Assert.Equal(SeriesStatus.Completed, series.Status);
        Assert.Equal(_challenger, series.WinnerId);
        Assert.Equal(7, series.Rounds.Count);
    }

    [Fact]
    public void Identical_result_retry()
    {
        var fixture = Load("10-identical-result-retry.json");
        var series = Run(fixture);

        var attempt = series.Rounds.Single(r => r.GameNumber == 1).AttemptFor(_challenger);
        Assert.Equal(50, attempt!.Result!.Value.Value);
        // Accept (0->1) + StartAttempt (1->2) + the first, genuinely-new CompleteAttempt (2->3)
        // each bump the revision; the second, idempotent CompleteAttempt must not bump it again.
        Assert.Equal(3, series.Revision);
    }

    /// <summary>
    /// Documents, rather than hides, the gap fixture 11 describes: the Backend's current
    /// CompleteAttempt cannot distinguish "identical retry" from "conflicting replay" - both take
    /// the idempotent-return path with no comparison against the newly submitted payload. This
    /// test pins today's actual (non-compliant) behavior so #11 has a failing assertion to flip
    /// once it adds payload comparison, instead of silently discovering the gap later.
    /// </summary>
    [Fact]
    public void Conflicting_result_replay_is_not_yet_rejected_known_gap_for_issue_11()
    {
        var fixture = Load("11-conflicting-result-replay.json");
        Assert.False(fixture.BackendExecutable);

        var series = VersusSeries.CreateChallenge(_challenger, _opponent, SeriesFormat.BestOf(1), Now);
        series.Accept(_opponent, Now);
        series.StartAttempt(_challenger, 1, Now);
        series.CompleteAttempt(_challenger, 1, Score.Of(50), Now);

        // Protocol requires this to be rejected as a conflict (HTTP 409). It is not, today.
        var second = series.CompleteAttempt(_challenger, 1, Score.Of(999), Now);

        Assert.Equal(50, second.Result!.Value.Value); // silently kept the first value instead of rejecting
    }

    private VersusSeries Run(FixtureFile fixture, bool stopBeforeExpectedRejections = false)
    {
        Assert.True(fixture.BackendExecutable, $"Fixture '{fixture.FixtureId}' is not marked backend-executable.");

        VersusSeries? series = null;
        foreach (var command in fixture.Commands)
        {
            if (command.Op.EndsWith("ExpectRejected", StringComparison.Ordinal))
            {
                if (stopBeforeExpectedRejections)
                {
                    continue;
                }

                throw new NotSupportedException(
                    $"Fixture '{fixture.FixtureId}' command '{command.Op}' needs explicit rejection handling in this runner.");
            }

            var actor = ResolveActor(command.By);
            switch (command.Op)
            {
                case "createChallenge":
                    series = VersusSeries.CreateChallenge(_challenger, _opponent, SeriesFormat.BestOf(fixture.SeriesFormat.TotalGames), Now);
                    break;
                case "accept":
                    series!.Accept(actor, Now);
                    break;
                case "decline":
                    series!.Decline(actor, Now);
                    break;
                case "cancel":
                    series!.Cancel(actor, Now);
                    break;
                case "startAttempt":
                    series!.StartAttempt(actor, command.Game!.Value, Now);
                    break;
                case "completeAttempt":
                    series!.CompleteAttempt(actor, command.Game!.Value, ScoreOf(fixture.FixtureId, command.Result!), Now);
                    break;
                default:
                    throw new NotSupportedException($"Fixture command '{command.Op}' is not supported by the Backend-executable runner.");
            }
        }

        return series ?? throw new InvalidOperationException($"Fixture '{fixture.FixtureId}' never issued createChallenge.");
    }

    private PlayerId ResolveActor(string? by) => by switch
    {
        "challenger" => _challenger,
        "opponent" => _opponent,
        _ => throw new NotSupportedException($"Unknown actor role '{by}'.")
    };

    private static Score ScoreOf(string fixtureId, Dictionary<string, JsonElement> result)
    {
        if (!result.TryGetValue("Score", out var value))
        {
            throw new NotSupportedException(
                $"Fixture '{fixtureId}' submits a metric the Backend cannot carry yet (it only has a single int Score today - see issue #8 required changes for #11).");
        }

        return Score.Of(value.GetInt32());
    }

    private static FixtureFile Load(string fileName)
    {
        var path = Path.Combine(FixturesDirectory(), fileName);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<FixtureFile>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Fixture '{fileName}' deserialized to null.");
    }

    private static string FixturesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Level5BackendV2.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"Could not locate Level5BackendV2.sln by walking up from '{AppContext.BaseDirectory}'.");
        }

        return Path.Combine(directory.FullName, "docs", "competition-protocol", "fixtures");
    }

    private sealed record FixtureFile(
        string FixtureId,
        int ProtocolVersion,
        bool BackendExecutable,
        FixtureSeriesFormat SeriesFormat,
        List<FixtureCommand> Commands,
        JsonElement Expected);

    private sealed record FixtureSeriesFormat(int TotalGames);

    private sealed record FixtureCommand(string Op, string? By, int? Game, Dictionary<string, JsonElement>? Result);
}
