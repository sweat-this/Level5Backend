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
    [InlineData("09-unsupported-ruleset-version.json", "unsupported-ruleset-version", false)]
    [InlineData("01-sealed-attempt-one-completed.json", "sealed-attempt-one-completed", true)]
    [InlineData("02-open-target-primary-only.json", "open-target-primary-only", true)]
    [InlineData("03-higher-is-better-result.json", "higher-is-better-result", true)]
    [InlineData("04-lower-is-better-result.json", "lower-is-better-result", true)]
    [InlineData("05-ordered-multi-key-tiebreak.json", "ordered-multi-key-tiebreak", true)]
    [InlineData("06-true-draw.json", "true-draw", true)]
    [InlineData("07-best-of-3-early-termination.json", "best-of-3-early-termination", true)]
    [InlineData("08-best-of-7-full-run.json", "best-of-7-full-run", true)]
    [InlineData("10-identical-result-retry.json", "identical-result-retry", true)]
    [InlineData("11-conflicting-result-replay.json", "conflicting-result-replay", true)]
    public void Fixture_manifest_identity_and_executability_flag_are_unchanged(
        string fileName, string expectedFixtureId, bool expectedBackendExecutable)
    {
        var fixture = Load(fileName);

        Assert.Equal(expectedFixtureId, fixture.FixtureId);
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
        Assert.Null(round.OpponentAttempt.Result);
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

        // Run() executes every command, including the trailing startAttemptExpectRejected for
        // game 3 - it asserts that command is rejected as part of the fixture-driven run, rather
        // than the test re-deriving the rejection by hand afterwards.
        var series = Run(fixture);

        Assert.Equal(SeriesStatus.Completed, series.Status);
        Assert.Equal(_challenger, series.WinnerId);
        Assert.Equal(2, series.Rounds.Count);
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
        Assert.Equal(50d, attempt!.Result!.ValueOf(ResultMetric.Score));
        // Accept (0->1) + StartAttempt (1->2) + the first, genuinely-new CompleteAttempt (2->3)
        // each bump the revision; the second, idempotent CompleteAttempt must not bump it again.
        Assert.Equal(3, series.Revision);
    }

    [Fact]
    public void Open_target_primary_only_reveal()
    {
        var fixture = Load("02-open-target-primary-only.json");
        var series = Run(fixture);

        var opponentView = series.ToView(_opponent);
        var round = opponentView.Games.Single(g => g.GameNumber == 1);

        Assert.NotNull(round.OpponentAttempt!.Result);
        Assert.Single(round.OpponentAttempt.Result);
        Assert.Equal(42d, round.OpponentAttempt.Result[ResultMetric.Score]);
        Assert.Equal(SeriesStatus.Active, series.Status);
    }

    [Fact]
    public void Lower_is_better_result()
    {
        var fixture = Load("04-lower-is-better-result.json");
        var series = Run(fixture);

        Assert.Equal(SeriesStatus.Completed, series.Status);
        Assert.Equal(_opponent, series.WinnerId);
    }

    [Fact]
    public void Ordered_multi_key_tiebreak()
    {
        var fixture = Load("05-ordered-multi-key-tiebreak.json");
        var series = Run(fixture);

        Assert.Equal(SeriesStatus.Completed, series.Status);
        Assert.Equal(_opponent, series.WinnerId);
    }

    /// <summary>
    /// Fixture 11 is now <c>backendExecutable</c>: <see cref="VersusSeries.CompleteAttempt"/>
    /// compares an incoming result against the already-accepted one before taking the idempotent
    /// path, so a materially different resubmission is rejected as a conflict (issue #11) rather
    /// than silently discarded. <see cref="Run"/>'s generic <c>completeAttemptExpectRejected</c>
    /// handling already proves the rejection; this test additionally proves the accepted result
    /// was left untouched by the rejected retry.
    /// </summary>
    [Fact]
    public void Conflicting_result_replay_is_rejected()
    {
        var fixture = Load("11-conflicting-result-replay.json");
        var series = Run(fixture);

        var attempt = series.Rounds.Single(r => r.GameNumber == 1).AttemptFor(_challenger);
        Assert.Equal(50d, attempt!.Result!.ValueOf(ResultMetric.Score));
    }

    /// <summary>
    /// Drives a <see cref="VersusSeries"/> through every command in a
    /// <c>"backendExecutable": true</c> fixture. A command named <c>*ExpectRejected</c> is
    /// asserted to throw rather than being skipped, so a fixture's encoded rejection is proven by
    /// the same data-driven run as everything else - not re-derived by hand in the calling test.
    /// </summary>
    private VersusSeries Run(FixtureFile fixture)
    {
        Assert.True(fixture.BackendExecutable, $"Fixture '{fixture.FixtureId}' is not marked backend-executable.");

        VersusSeries? series = null;
        foreach (var command in fixture.Commands)
        {
            var actor = ResolveActor(command.By);
            var isExpectedRejection = command.Op.EndsWith("ExpectRejected", StringComparison.Ordinal);
            var op = isExpectedRejection ? command.Op[..^"ExpectRejected".Length] : command.Op;

            void Apply()
            {
                switch (op)
                {
                    case "createChallenge":
                        series = VersusSeries.CreateChallenge(_challenger, _opponent, SeriesFormat.BestOf(fixture.SeriesFormat.TotalGames), RulesFrom(fixture.Ruleset), Now);
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
                        var attemptId = series!.Rounds.Single(r => r.GameNumber == command.Game!.Value).AttemptFor(actor)!.Id;
                        series.CompleteAttempt(actor, command.Game!.Value, attemptId, ResultOf(fixture.FixtureId, command.Result!), Now);
                        break;
                    default:
                        throw new NotSupportedException($"Fixture command '{op}' is not supported by the Backend-executable runner.");
                }
            }

            if (isExpectedRejection)
            {
                Assert.ThrowsAny<Exception>(Apply);
            }
            else
            {
                Apply();
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

    private static AttemptResult ResultOf(string fixtureId, Dictionary<string, JsonElement> result)
    {
        var metrics = new Dictionary<ResultMetric, double>();
        foreach (var (key, value) in result)
        {
            if (!Enum.TryParse<ResultMetric>(key, out var metric))
            {
                throw new NotSupportedException($"Fixture '{fixtureId}' submits an unrecognized metric '{key}'.");
            }

            metrics[metric] = value.GetDouble();
        }

        return AttemptResult.Of(metrics);
    }

    private static FrozenRules RulesFrom(FixtureRuleset ruleset) => FrozenRules.Create(
        CompetitionProtocol.CurrentVersion,
        ruleset.RulesetId,
        ruleset.RulesetVersion,
        ruleset.CatalogMinimumCompatibleVersion ?? 1,
        modeId: $"mode-{ruleset.RulesetId}",
        Enum.Parse<InformationPolicy>(ruleset.InformationPolicy),
        ruleset.AlternatesFirstAttempt,
        [.. ruleset.ComparisonKeys.Select(k => new ComparisonKey(Enum.Parse<ResultMetric>(k.Metric), Enum.Parse<MetricDirection>(k.Direction)))]);

    private static FixtureFile Load(string fileName)
    {
        var path = Path.Combine(FixturesDirectory(), fileName);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<FixtureFile>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Fixture '{fileName}' deserialized to null.");
    }

    /// <summary>
    /// Locates <c>v2/docs/competition-protocol/fixtures/</c> relative to the repository root.
    /// Tries the test assembly's own output directory first (correct for <c>dotnet test</c> run
    /// in place, which is how this project is run today), then falls back to the process's
    /// current working directory, in case a future CI step stages test binaries away from the
    /// source tree before running them.
    /// </summary>
    private static string FixturesDirectory()
    {
        return FindFixturesDirectoryFrom(AppContext.BaseDirectory)
            ?? FindFixturesDirectoryFrom(Directory.GetCurrentDirectory())
            ?? throw new InvalidOperationException(
                $"Could not locate Level5BackendV2.sln by walking up from either " +
                $"'{AppContext.BaseDirectory}' or the current directory '{Directory.GetCurrentDirectory()}'.");
    }

    private static string? FindFixturesDirectoryFrom(string startingDirectory)
    {
        var directory = new DirectoryInfo(startingDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Level5BackendV2.sln")))
        {
            directory = directory.Parent;
        }

        return directory is null ? null : Path.Combine(directory.FullName, "docs", "competition-protocol", "fixtures");
    }

    private sealed record FixtureFile(
        string FixtureId,
        int ProtocolVersion,
        bool BackendExecutable,
        FixtureSeriesFormat SeriesFormat,
        FixtureRuleset Ruleset,
        List<FixtureCommand> Commands,
        JsonElement Expected);

    private sealed record FixtureSeriesFormat(int TotalGames);

    private sealed record FixtureRuleset(
        string RulesetId,
        int RulesetVersion,
        int? CatalogMinimumCompatibleVersion,
        string InformationPolicy,
        bool AlternatesFirstAttempt,
        List<FixtureComparisonKey> ComparisonKeys);

    private sealed record FixtureComparisonKey(string Metric, string Direction);

    private sealed record FixtureCommand(string Op, string? By, int? Game, Dictionary<string, JsonElement>? Result);
}
