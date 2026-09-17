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
    [InlineData("02-open-target-primary-only.json", "open-target-primary-only", false)]
    [InlineData("04-lower-is-better-result.json", "lower-is-better-result", false)]
    [InlineData("05-ordered-multi-key-tiebreak.json", "ordered-multi-key-tiebreak", false)]
    [InlineData("09-unsupported-ruleset-version.json", "unsupported-ruleset-version", false)]
    [InlineData("11-conflicting-result-replay.json", "conflicting-result-replay", false)]
    [InlineData("01-sealed-attempt-one-completed.json", "sealed-attempt-one-completed", true)]
    [InlineData("03-higher-is-better-result.json", "higher-is-better-result", true)]
    [InlineData("06-true-draw.json", "true-draw", true)]
    [InlineData("07-best-of-3-early-termination.json", "best-of-3-early-termination", true)]
    [InlineData("08-best-of-7-full-run.json", "best-of-7-full-run", true)]
    [InlineData("10-identical-result-retry.json", "identical-result-retry", true)]
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
    ///
    /// Fixture 11 is not <c>backendExecutable</c> - by design, since what it proves is that the
    /// Backend does NOT reject the conflicting command, which is the opposite of what
    /// <see cref="Run"/>'s generic *ExpectRejected handling asserts. This test therefore drives
    /// the series from the fixture's own commands directly (never hardcoding the scores or game
    /// number as separate literals) so a future edit to the fixture's content is reflected here
    /// automatically instead of silently diverging from what the test actually exercises.
    /// </summary>
    [Fact]
    public void Conflicting_result_replay_is_not_yet_rejected_known_gap_for_issue_11()
    {
        var fixture = Load("11-conflicting-result-replay.json");
        Assert.False(fixture.BackendExecutable);
        Assert.Equal(5, fixture.Commands.Count);

        var accept = fixture.Commands[1];
        var startAttempt = fixture.Commands[2];
        var firstComplete = fixture.Commands[3];
        var conflictingComplete = fixture.Commands[4];
        Assert.Equal("completeAttempt", firstComplete.Op);
        Assert.Equal("completeAttemptExpectRejected", conflictingComplete.Op);

        var series = VersusSeries.CreateChallenge(_challenger, _opponent, SeriesFormat.BestOf(fixture.SeriesFormat.TotalGames), Now);
        series.Accept(ResolveActor(accept.By), Now);
        series.StartAttempt(ResolveActor(startAttempt.By), startAttempt.Game!.Value, Now);
        var firstScore = ScoreOf(fixture.FixtureId, firstComplete.Result!);
        series.CompleteAttempt(ResolveActor(firstComplete.By), firstComplete.Game!.Value, firstScore, Now);

        // Protocol requires this to be rejected as a conflict (HTTP 409). It is not, today: it
        // silently returns the FIRST accepted result instead of throwing or rejecting.
        var conflictingScore = ScoreOf(fixture.FixtureId, conflictingComplete.Result!);
        Assert.NotEqual(firstScore, conflictingScore); // the fixture must actually describe a conflict, not a retry
        var second = series.CompleteAttempt(ResolveActor(conflictingComplete.By), conflictingComplete.Game!.Value, conflictingScore, Now);

        Assert.Equal(firstScore.Value, second.Result!.Value.Value);
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
        List<FixtureCommand> Commands,
        JsonElement Expected);

    private sealed record FixtureSeriesFormat(int TotalGames);

    private sealed record FixtureCommand(string Op, string? By, int? Game, Dictionary<string, JsonElement>? Result);
}
