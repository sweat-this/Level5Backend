using Level5.Domain.Competition;
using Level5.Domain.Ids;
using Xunit;

namespace Level5.Domain.Tests.Competition;

public class VersusSeriesTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly FrozenRules DefaultRules = FrozenRules.Create(
        CompetitionProtocol.CurrentVersion, "score-only", 1, 1, "mode-score-only",
        InformationPolicy.SealedAttempt, alternatesFirstAttempt: false,
        [new ComparisonKey(ResultMetric.Score, MetricDirection.HigherWins)]);

    private static readonly FrozenRules LowerWinsRules = FrozenRules.Create(
        CompetitionProtocol.CurrentVersion, "race", 1, 1, "mode-race",
        InformationPolicy.SealedAttempt, alternatesFirstAttempt: false,
        [new ComparisonKey(ResultMetric.CompletionTimeSeconds, MetricDirection.LowerWins)]);

    private static readonly FrozenRules TieBreakRules = FrozenRules.Create(
        CompetitionProtocol.CurrentVersion, "contest", 1, 1, "mode-contest",
        InformationPolicy.SealedAttempt, alternatesFirstAttempt: false,
        [
            new ComparisonKey(ResultMetric.Score, MetricDirection.HigherWins),
            new ComparisonKey(ResultMetric.CompletionTimeSeconds, MetricDirection.LowerWins),
            new ComparisonKey(ResultMetric.Accuracy, MetricDirection.HigherWins)
        ]);

    private static readonly FrozenRules OpenTargetRules = FrozenRules.Create(
        CompetitionProtocol.CurrentVersion, "contest", 1, 1, "mode-contest",
        InformationPolicy.OpenTarget, alternatesFirstAttempt: false,
        [
            new ComparisonKey(ResultMetric.Score, MetricDirection.HigherWins),
            new ComparisonKey(ResultMetric.CompletionTimeSeconds, MetricDirection.LowerWins)
        ]);

    private static readonly FrozenRules AlternatingOpenTargetRules = FrozenRules.Create(
        CompetitionProtocol.CurrentVersion, "contest", 1, 1, "mode-contest",
        InformationPolicy.OpenTarget, alternatesFirstAttempt: true,
        [new ComparisonKey(ResultMetric.Score, MetricDirection.HigherWins)]);

    private readonly PlayerId _challenger = PlayerId.New();
    private readonly PlayerId _opponent = PlayerId.New();

    private VersusSeries CreateBestOf(int totalGames, FrozenRules? rules = null)
        => VersusSeries.CreateChallenge(_challenger, _opponent, SeriesFormat.BestOf(totalGames), rules ?? DefaultRules, Now);

    [Fact]
    public void CreateChallenge_starts_pending_acceptance_with_revision_zero()
    {
        var series = CreateBestOf(3);

        Assert.Equal(SeriesStatus.PendingAcceptance, series.Status);
        Assert.Equal(0, series.Revision);
        Assert.Equal(1, series.CurrentGameNumber);
    }

    [Fact]
    public void CreateChallenge_freezes_the_given_rules_unchanged()
    {
        var series = CreateBestOf(3);

        Assert.Equal(DefaultRules, series.Rules);
    }

    [Fact]
    public void CreateChallenge_against_self_is_rejected()
    {
        Assert.Throws<InvalidChallengeException>(() =>
            VersusSeries.CreateChallenge(_challenger, _challenger, SeriesFormat.BestOf(3), DefaultRules, Now));
    }

    [Fact]
    public void Accept_by_opponent_activates_series_and_bumps_revision()
    {
        var series = CreateBestOf(3);

        series.Accept(_opponent, Now);

        Assert.Equal(SeriesStatus.Active, series.Status);
        Assert.Equal(1, series.Revision);
    }

    [Fact]
    public void Accept_by_challenger_is_rejected()
    {
        var series = CreateBestOf(3);

        Assert.Throws<SeriesAuthorizationException>(() => series.Accept(_challenger, Now));
    }

    [Fact]
    public void Accept_by_non_participant_is_rejected()
    {
        var series = CreateBestOf(3);

        Assert.Throws<SeriesAuthorizationException>(() => series.Accept(PlayerId.New(), Now));
    }

    [Fact]
    public void Accept_retry_by_the_same_opponent_on_an_active_series_is_idempotent()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);
        var revisionAfterFirstAccept = series.Revision;

        series.Accept(_opponent, Now);

        Assert.Equal(SeriesStatus.Active, series.Status);
        Assert.Equal(revisionAfterFirstAccept, series.Revision);
    }

    [Fact]
    public void Accept_by_the_challenger_on_an_already_active_series_is_still_forbidden()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);

        Assert.Throws<SeriesAuthorizationException>(() => series.Accept(_challenger, Now));
    }

    [Fact]
    public void Accept_on_a_declined_series_is_an_illegal_transition()
    {
        var series = CreateBestOf(3);
        series.Decline(_opponent, Now);

        Assert.Throws<IllegalSeriesTransitionException>(() => series.Accept(_opponent, Now));
    }

    [Fact]
    public void Accept_on_a_cancelled_series_is_an_illegal_transition()
    {
        var series = CreateBestOf(3);
        series.Cancel(_challenger, Now);

        Assert.Throws<IllegalSeriesTransitionException>(() => series.Accept(_opponent, Now));
    }

    /// <summary>Drives a fresh best-of-1 series into <paramref name="status"/> through its legal transitions only.</summary>
    private VersusSeries SeriesIn(SeriesStatus status)
    {
        var series = CreateBestOf(1);
        switch (status)
        {
            case SeriesStatus.Active:
                series.Accept(_opponent, Now);
                break;
            case SeriesStatus.Completed:
                series.Accept(_opponent, Now);
                PlayScores(series, gameNumber: 1, challengerScore: 10, opponentScore: 10);
                break;
            case SeriesStatus.Declined:
                series.Decline(_opponent, Now);
                break;
            case SeriesStatus.Cancelled:
                series.Cancel(_challenger, Now);
                break;
            case SeriesStatus.Expired:
                series.Expire(Now);
                break;
        }

        Assert.Equal(status, series.Status);
        return series;
    }

    /// <summary>A replayed command must be a pure no-op: no revision bump (so callers skip the write) and no timestamp change.</summary>
    private static void AssertReplayIsNoOp(VersusSeries series, Action replay)
    {
        var (status, revision, updatedAt, completedAt) = (series.Status, series.Revision, series.UpdatedAt, series.CompletedAt);

        replay();

        Assert.Equal(status, series.Status);
        Assert.Equal(revision, series.Revision);
        Assert.Equal(updatedAt, series.UpdatedAt);
        Assert.Equal(completedAt, series.CompletedAt);
    }

    private static readonly DateTimeOffset Later = Now.AddMinutes(5);

    [Theory]
    [InlineData(SeriesStatus.Active)]
    [InlineData(SeriesStatus.Completed)] // Completed is only reachable from Active, i.e. after this same Accept won.
    public void Accept_replay_by_the_opponent_is_a_no_op(SeriesStatus status)
    {
        var series = SeriesIn(status);

        AssertReplayIsNoOp(series, () => series.Accept(_opponent, Later));
    }

    [Theory]
    [InlineData(SeriesStatus.Declined)]
    [InlineData(SeriesStatus.Cancelled)]
    [InlineData(SeriesStatus.Expired)]
    public void Accept_after_a_different_command_won_is_an_illegal_transition(SeriesStatus status)
    {
        var series = SeriesIn(status);

        Assert.Throws<IllegalSeriesTransitionException>(() => series.Accept(_opponent, Later));
    }

    [Fact]
    public void Decline_replay_by_the_opponent_is_a_no_op()
    {
        var series = SeriesIn(SeriesStatus.Declined);

        AssertReplayIsNoOp(series, () => series.Decline(_opponent, Later));
    }

    [Theory]
    [InlineData(SeriesStatus.Active)]
    [InlineData(SeriesStatus.Completed)]
    [InlineData(SeriesStatus.Cancelled)]
    [InlineData(SeriesStatus.Expired)]
    public void Decline_after_a_different_command_won_is_an_illegal_transition(SeriesStatus status)
    {
        var series = SeriesIn(status);

        Assert.Throws<IllegalSeriesTransitionException>(() => series.Decline(_opponent, Later));
    }

    [Fact]
    public void Cancel_replay_by_the_challenger_is_a_no_op()
    {
        var series = SeriesIn(SeriesStatus.Cancelled);

        AssertReplayIsNoOp(series, () => series.Cancel(_challenger, Later));
    }

    [Theory]
    [InlineData(SeriesStatus.Active)]
    [InlineData(SeriesStatus.Completed)]
    [InlineData(SeriesStatus.Declined)]
    [InlineData(SeriesStatus.Expired)]
    public void Cancel_after_a_different_command_won_is_an_illegal_transition(SeriesStatus status)
    {
        var series = SeriesIn(status);

        Assert.Throws<IllegalSeriesTransitionException>(() => series.Cancel(_challenger, Later));
    }

    [Fact]
    public void Expire_from_pending_acceptance_transitions_and_sets_the_terminal_timestamp()
    {
        var series = CreateBestOf(3);

        series.Expire(Later);

        Assert.Equal(SeriesStatus.Expired, series.Status);
        Assert.Equal(Later, series.CompletedAt);
        Assert.Equal(1, series.Revision);
    }

    [Fact]
    public void Expire_replay_is_a_no_op()
    {
        var series = SeriesIn(SeriesStatus.Expired);

        AssertReplayIsNoOp(series, () => series.Expire(Later));
    }

    [Theory]
    [InlineData(SeriesStatus.Active)]
    [InlineData(SeriesStatus.Completed)]
    [InlineData(SeriesStatus.Declined)]
    [InlineData(SeriesStatus.Cancelled)]
    public void Expire_after_a_different_command_won_is_an_illegal_transition(SeriesStatus status)
    {
        var series = SeriesIn(status);

        Assert.Throws<IllegalSeriesTransitionException>(() => series.Expire(Later));
    }

    /// <summary>Replay tolerance never widens authorization: the wrong actor is rejected in every state, including the one where the right actor would replay.</summary>
    [Theory]
    [InlineData(SeriesStatus.PendingAcceptance)]
    [InlineData(SeriesStatus.Active)]
    [InlineData(SeriesStatus.Completed)]
    [InlineData(SeriesStatus.Declined)]
    [InlineData(SeriesStatus.Cancelled)]
    [InlineData(SeriesStatus.Expired)]
    public void Wrong_actors_are_rejected_for_every_challenge_transition_in_every_state(SeriesStatus status)
    {
        var series = SeriesIn(status);
        var stranger = PlayerId.New();

        Assert.Throws<SeriesAuthorizationException>(() => series.Accept(_challenger, Later));
        Assert.Throws<SeriesAuthorizationException>(() => series.Accept(stranger, Later));
        Assert.Throws<SeriesAuthorizationException>(() => series.Decline(_challenger, Later));
        Assert.Throws<SeriesAuthorizationException>(() => series.Decline(stranger, Later));
        Assert.Throws<SeriesAuthorizationException>(() => series.Cancel(_opponent, Later));
        Assert.Throws<SeriesAuthorizationException>(() => series.Cancel(stranger, Later));
        Assert.Equal(status, series.Status);
    }

    [Fact]
    public void Decline_by_challenger_is_rejected()
    {
        var series = CreateBestOf(3);

        Assert.Throws<SeriesAuthorizationException>(() => series.Decline(_challenger, Now));
    }

    [Fact]
    public void Cancel_by_opponent_is_rejected()
    {
        var series = CreateBestOf(3);

        Assert.Throws<SeriesAuthorizationException>(() => series.Cancel(_opponent, Now));
    }

    [Fact]
    public void Cancel_after_acceptance_is_rejected()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);

        Assert.Throws<IllegalSeriesTransitionException>(() => series.Cancel(_challenger, Now));
    }

    [Fact]
    public void StartAttempt_before_acceptance_is_rejected()
    {
        var series = CreateBestOf(3);

        Assert.Throws<IllegalSeriesTransitionException>(() => series.StartAttempt(_challenger, 1, Now));
    }

    [Fact]
    public void StartAttempt_by_non_participant_is_rejected()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);

        Assert.Throws<SeriesAuthorizationException>(() => series.StartAttempt(PlayerId.New(), 1, Now));
    }

    [Fact]
    public void StartAttempt_for_a_future_game_number_is_rejected()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);

        Assert.Throws<IllegalSeriesTransitionException>(() => series.StartAttempt(_challenger, 2, Now));
    }

    [Fact]
    public void StartAttempt_for_game_two_is_rejected_while_game_one_is_still_unresolved()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);
        var challengerAttempt = series.StartAttempt(_challenger, 1, Now);
        series.StartAttempt(_opponent, 1, Now);
        series.CompleteAttempt(_challenger, 1, challengerAttempt.Id, AttemptResult.OfScore(50), Now);
        // Opponent has not completed their game-1 attempt yet, so the round - and the series -
        // has not advanced past game 1.

        Assert.Throws<IllegalSeriesTransitionException>(() => series.StartAttempt(_challenger, 2, Now));
    }

    [Fact]
    public void StartAttempt_twice_for_the_same_player_is_idempotent_and_does_not_bump_revision()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);
        var revisionAfterAccept = series.Revision;

        var first = series.StartAttempt(_challenger, 1, Now);
        var revisionAfterFirstStart = series.Revision;
        var second = series.StartAttempt(_challenger, 1, Now);

        Assert.Equal(first.Id, second.Id);
        Assert.True(revisionAfterFirstStart > revisionAfterAccept);
        Assert.Equal(revisionAfterFirstStart, series.Revision);
    }

    [Fact]
    public void CompleteAttempt_before_starting_is_rejected()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);

        Assert.Throws<AttemptNotStartedException>(() => series.CompleteAttempt(_challenger, 1, AttemptId.New(), AttemptResult.OfScore(10), Now));
    }

    [Fact]
    public void CompleteAttempt_with_an_attemptId_the_player_never_started_is_rejected()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);
        series.StartAttempt(_challenger, 1, Now);

        Assert.Throws<AttemptIdentityMismatchException>(
            () => series.CompleteAttempt(_challenger, 1, AttemptId.New(), AttemptResult.OfScore(10), Now));
    }

    [Fact]
    public void CompleteAttempt_twice_with_the_same_result_is_idempotent_and_keeps_the_original_result()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);
        var started = series.StartAttempt(_challenger, 1, Now);

        var first = series.CompleteAttempt(_challenger, 1, started.Id, AttemptResult.OfScore(50), Now);
        var revisionAfterFirstComplete = series.Revision;
        var second = series.CompleteAttempt(_challenger, 1, started.Id, AttemptResult.OfScore(50), Now);

        Assert.Equal(50d, first.Result!.ValueOf(ResultMetric.Score));
        Assert.Equal(50d, second.Result!.ValueOf(ResultMetric.Score));
        Assert.Equal(revisionAfterFirstComplete, series.Revision);
    }

    [Fact]
    public void CompleteAttempt_twice_with_a_different_result_is_rejected_as_a_conflict()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);
        var started = series.StartAttempt(_challenger, 1, Now);
        series.CompleteAttempt(_challenger, 1, started.Id, AttemptResult.OfScore(50), Now);

        Assert.Throws<ConflictingAttemptResultException>(
            () => series.CompleteAttempt(_challenger, 1, started.Id, AttemptResult.OfScore(999), Now));

        // The conflicting retry must not have replaced the accepted result.
        var attempt = series.Rounds.Single(r => r.GameNumber == 1).AttemptFor(_challenger);
        Assert.Equal(50d, attempt!.Result!.ValueOf(ResultMetric.Score));
    }

    [Fact]
    public void CompleteAttempt_missing_a_required_comparison_metric_is_rejected()
    {
        var series = CreateBestOf(1, TieBreakRules);
        series.Accept(_opponent, Now);
        var started = series.StartAttempt(_challenger, 1, Now);

        var incompleteResult = AttemptResult.Of(new Dictionary<ResultMetric, double> { [ResultMetric.Score] = 50 });

        Assert.Throws<MissingRequiredMetricException>(
            () => series.CompleteAttempt(_challenger, 1, started.Id, incompleteResult, Now));
    }

    [Fact]
    public void Round_does_not_resolve_until_both_participants_complete()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);
        var challengerAttempt = series.StartAttempt(_challenger, 1, Now);
        series.StartAttempt(_opponent, 1, Now);

        series.CompleteAttempt(_challenger, 1, challengerAttempt.Id, AttemptResult.OfScore(50), Now);

        Assert.False(series.Rounds.Single(r => r.GameNumber == 1).IsResolved);
        Assert.Equal(1, series.CurrentGameNumber);
        Assert.Equal(SeriesStatus.Active, series.Status);
    }

    [Fact]
    public void Winning_enough_games_completes_the_series()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);

        PlayChallengerWin(series, gameNumber: 1);
        Assert.Equal(SeriesStatus.Active, series.Status);
        Assert.Equal(2, series.CurrentGameNumber);

        PlayChallengerWin(series, gameNumber: 2);

        Assert.Equal(SeriesStatus.Completed, series.Status);
        Assert.Equal(_challenger, series.WinnerId);
    }

    [Fact]
    public void Best_of_seven_completes_exactly_once_after_the_fourth_win()
    {
        var series = CreateBestOf(7);
        series.Accept(_opponent, Now);

        for (var game = 1; game <= 4; game++)
        {
            PlayChallengerWin(series, game);
        }

        Assert.Equal(SeriesStatus.Completed, series.Status);
        Assert.Equal(_challenger, series.WinnerId);
        Assert.Equal(4, series.Rounds.Count);
        var revisionAtCompletion = series.Revision;

        // Games 5-7 were never activated - starting one is illegal, not merely a no-op, since
        // the series is no longer Active.
        Assert.Throws<IllegalSeriesTransitionException>(() => series.StartAttempt(_challenger, 5, Now));
        Assert.Equal(revisionAtCompletion, series.Revision);
    }

    [Fact]
    public void Exhausting_every_game_on_ties_completes_the_series_as_a_draw()
    {
        var series = CreateBestOf(1);
        series.Accept(_opponent, Now);

        PlayScores(series, gameNumber: 1, challengerScore: 10, opponentScore: 10);

        Assert.Equal(SeriesStatus.Completed, series.Status);
        Assert.Null(series.WinnerId);
    }

    [Theory]
    [InlineData(80, 60, true)]
    [InlineData(60, 80, false)]
    public void Higher_wins_comparison_direction_decides_the_round(double challengerScore, double opponentScore, bool challengerShouldWin)
    {
        var series = CreateBestOf(1);
        series.Accept(_opponent, Now);

        PlayScores(series, gameNumber: 1, challengerScore, opponentScore);

        Assert.Equal(challengerShouldWin ? _challenger : _opponent, series.WinnerId);
    }

    [Fact]
    public void Lower_wins_comparison_direction_favors_the_smaller_value()
    {
        var series = CreateBestOf(1, LowerWinsRules);
        series.Accept(_opponent, Now);

        var challengerAttempt = series.StartAttempt(_challenger, 1, Now);
        var opponentAttempt = series.StartAttempt(_opponent, 1, Now);
        series.CompleteAttempt(_challenger, 1, challengerAttempt.Id, AttemptResult.Of(new Dictionary<ResultMetric, double> { [ResultMetric.CompletionTimeSeconds] = 55.2 }), Now);
        series.CompleteAttempt(_opponent, 1, opponentAttempt.Id, AttemptResult.Of(new Dictionary<ResultMetric, double> { [ResultMetric.CompletionTimeSeconds] = 40.0 }), Now);

        Assert.Equal(_opponent, series.WinnerId);
    }

    [Fact]
    public void Ordered_comparison_keys_fall_through_to_the_next_key_on_a_tie()
    {
        var series = CreateBestOf(1, TieBreakRules);
        series.Accept(_opponent, Now);

        var challengerAttempt = series.StartAttempt(_challenger, 1, Now);
        var opponentAttempt = series.StartAttempt(_opponent, 1, Now);

        // Score ties at 50; CompletionTimeSeconds (LowerWins) breaks the tie in the opponent's favor.
        series.CompleteAttempt(_challenger, 1, challengerAttempt.Id, AttemptResult.Of(new Dictionary<ResultMetric, double>
        {
            [ResultMetric.Score] = 50,
            [ResultMetric.CompletionTimeSeconds] = 20.0,
            [ResultMetric.Accuracy] = 80
        }), Now);
        series.CompleteAttempt(_opponent, 1, opponentAttempt.Id, AttemptResult.Of(new Dictionary<ResultMetric, double>
        {
            [ResultMetric.Score] = 50,
            [ResultMetric.CompletionTimeSeconds] = 15.0,
            [ResultMetric.Accuracy] = 60
        }), Now);

        Assert.Equal(_opponent, series.WinnerId);
    }

    [Fact]
    public void Every_comparison_key_tying_is_a_true_draw()
    {
        var series = CreateBestOf(1, TieBreakRules);
        series.Accept(_opponent, Now);

        var challengerAttempt = series.StartAttempt(_challenger, 1, Now);
        var opponentAttempt = series.StartAttempt(_opponent, 1, Now);
        var tiedResult = AttemptResult.Of(new Dictionary<ResultMetric, double>
        {
            [ResultMetric.Score] = 50,
            [ResultMetric.CompletionTimeSeconds] = 20.0,
            [ResultMetric.Accuracy] = 80
        });
        series.CompleteAttempt(_challenger, 1, challengerAttempt.Id, tiedResult, Now);
        series.CompleteAttempt(_opponent, 1, opponentAttempt.Id, tiedResult, Now);

        Assert.Equal(SeriesStatus.Completed, series.Status);
        Assert.Null(series.WinnerId);
    }

    [Fact]
    public void ToView_seals_every_opponent_metric_until_the_round_resolves()
    {
        var series = CreateBestOf(3, TieBreakRules);
        series.Accept(_opponent, Now);
        var challengerAttempt = series.StartAttempt(_challenger, 1, Now);
        series.StartAttempt(_opponent, 1, Now);
        series.CompleteAttempt(_challenger, 1, challengerAttempt.Id, AttemptResult.Of(new Dictionary<ResultMetric, double>
        {
            [ResultMetric.Score] = 77,
            [ResultMetric.CompletionTimeSeconds] = 12.5,
            [ResultMetric.Accuracy] = 90
        }), Now);

        var opponentView = series.ToView(_opponent);
        var round = opponentView.Games.Single(g => g.GameNumber == 1);

        // The opponent (viewer) has not completed their attempt, so the round is unresolved: the
        // challenger's attempt shows as completed (state is not secret) but none of its metrics -
        // not just Score - may leak.
        Assert.Equal(Domain.Competition.AttemptStatus.Completed, round.OpponentAttempt!.Status);
        Assert.Null(round.OpponentAttempt.Result);
    }

    [Fact]
    public void ToView_reveals_both_scores_once_the_round_resolves()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);
        var challengerAttempt = series.StartAttempt(_challenger, 1, Now);
        var opponentAttempt = series.StartAttempt(_opponent, 1, Now);
        series.CompleteAttempt(_challenger, 1, challengerAttempt.Id, AttemptResult.OfScore(77), Now);
        series.CompleteAttempt(_opponent, 1, opponentAttempt.Id, AttemptResult.OfScore(60), Now);

        var opponentView = series.ToView(_opponent);
        var round = opponentView.Games.Single(g => g.GameNumber == 1);

        Assert.Equal(77d, round.OpponentAttempt!.Result![ResultMetric.Score]);
        Assert.Equal(60d, round.YourAttempt!.Result![ResultMetric.Score]);
    }

    [Fact]
    public void ToView_exposes_the_frozen_rules()
    {
        var series = CreateBestOf(3);

        var view = series.ToView(_challenger);

        Assert.Equal(DefaultRules, view.Rules);
    }

    [Fact]
    public void CompleteAttempt_retry_after_that_completion_finished_the_series_still_returns_the_result()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);
        PlayChallengerWin(series, gameNumber: 1);

        var challengerAttempt = series.StartAttempt(_challenger, 2, Now);
        var opponentAttempt = series.StartAttempt(_opponent, 2, Now);
        series.CompleteAttempt(_challenger, 2, challengerAttempt.Id, AttemptResult.OfScore(100), Now);
        series.CompleteAttempt(_opponent, 2, opponentAttempt.Id, AttemptResult.OfScore(10), Now);

        Assert.Equal(SeriesStatus.Completed, series.Status);

        // The client's HTTP response for that last completion never arrived, so it retries the
        // exact same call. The series is no longer Active, but the retry must still succeed and
        // return the original result rather than fail because the series has moved on.
        var retried = series.CompleteAttempt(_opponent, 2, opponentAttempt.Id, AttemptResult.OfScore(10), Now);

        Assert.Equal(10d, retried.Result!.ValueOf(ResultMetric.Score));
    }

    [Fact]
    public void StartAttempt_retry_after_the_series_completed_still_returns_the_attempt()
    {
        var series = CreateBestOf(1);
        series.Accept(_opponent, Now);
        var started = series.StartAttempt(_challenger, 1, Now);
        var opponentAttempt = series.StartAttempt(_opponent, 1, Now);
        series.CompleteAttempt(_challenger, 1, started.Id, AttemptResult.OfScore(100), Now);
        series.CompleteAttempt(_opponent, 1, opponentAttempt.Id, AttemptResult.OfScore(10), Now);

        Assert.Equal(SeriesStatus.Completed, series.Status);

        var retried = series.StartAttempt(_challenger, 1, Now);

        Assert.Equal(started.Id, retried.Id);
    }

    [Fact]
    public void ToView_is_rejected_for_non_participants()
    {
        var series = CreateBestOf(3);

        Assert.Throws<SeriesAuthorizationException>(() => series.ToView(PlayerId.New()));
    }

    [Fact]
    public void OpenTarget_responder_cannot_start_before_the_first_mover_completes()
    {
        var series = CreateBestOf(1, OpenTargetRules);
        series.Accept(_opponent, Now);

        Assert.Throws<OpenTargetIssuanceOrderException>(() => series.StartAttempt(_opponent, 1, Now));
    }

    [Fact]
    public void OpenTarget_first_mover_may_start_immediately()
    {
        var series = CreateBestOf(1, OpenTargetRules);
        series.Accept(_opponent, Now);

        var attempt = series.StartAttempt(_challenger, 1, Now);

        Assert.NotNull(attempt);
    }

    [Fact]
    public void OpenTarget_responder_may_start_once_the_first_mover_completes()
    {
        var series = CreateBestOf(1, OpenTargetRules);
        series.Accept(_opponent, Now);
        var firstMoverAttempt = series.StartAttempt(_challenger, 1, Now);
        series.CompleteAttempt(_challenger, 1, firstMoverAttempt.Id, AttemptResult.Of(new Dictionary<ResultMetric, double>
        {
            [ResultMetric.Score] = 42,
            [ResultMetric.CompletionTimeSeconds] = 30.5
        }), Now);

        var responderAttempt = series.StartAttempt(_opponent, 1, Now);

        Assert.NotNull(responderAttempt);
    }

    [Fact]
    public void OpenTarget_reveals_only_the_primary_metric_to_the_responder_before_resolution()
    {
        var series = CreateBestOf(1, OpenTargetRules);
        series.Accept(_opponent, Now);
        var firstMoverAttempt = series.StartAttempt(_challenger, 1, Now);
        series.CompleteAttempt(_challenger, 1, firstMoverAttempt.Id, AttemptResult.Of(new Dictionary<ResultMetric, double>
        {
            [ResultMetric.Score] = 42,
            [ResultMetric.CompletionTimeSeconds] = 30.5
        }), Now);
        series.StartAttempt(_opponent, 1, Now);

        var responderView = series.ToView(_opponent);
        var round = responderView.Games.Single(g => g.GameNumber == 1);

        Assert.NotNull(round.OpponentAttempt!.Result);
        var revealed = round.OpponentAttempt.Result;
        Assert.Single(revealed);
        Assert.Equal(42d, revealed[ResultMetric.Score]);
    }

    [Fact]
    public void OpenTarget_alternates_the_first_mover_by_game_number_when_configured()
    {
        var series = CreateBestOf(3, AlternatingOpenTargetRules);
        series.Accept(_opponent, Now);

        // Game 1: challenger is first mover (index 0) - the responder cannot even start until
        // the first mover completes, not merely starts.
        Assert.Throws<OpenTargetIssuanceOrderException>(() => series.StartAttempt(_opponent, 1, Now));
        var g1Challenger = series.StartAttempt(_challenger, 1, Now);
        Assert.Throws<OpenTargetIssuanceOrderException>(() => series.StartAttempt(_opponent, 1, Now));
        series.CompleteAttempt(_challenger, 1, g1Challenger.Id, AttemptResult.OfScore(10), Now);
        var g1Opponent = series.StartAttempt(_opponent, 1, Now);
        series.CompleteAttempt(_opponent, 1, g1Opponent.Id, AttemptResult.OfScore(1), Now);

        // Game 2: opponent is first mover (index 1) since AlternatesFirstAttempt is true.
        Assert.Throws<OpenTargetIssuanceOrderException>(() => series.StartAttempt(_challenger, 2, Now));
        var g2Opponent = series.StartAttempt(_opponent, 2, Now);
        Assert.NotNull(g2Opponent);
    }

    private void PlayChallengerWin(VersusSeries series, int gameNumber)
        => PlayScores(series, gameNumber, challengerScore: 100, opponentScore: 10);

    private void PlayScores(VersusSeries series, int gameNumber, double challengerScore, double opponentScore)
    {
        var challengerAttempt = series.StartAttempt(_challenger, gameNumber, Now);
        var opponentAttempt = series.StartAttempt(_opponent, gameNumber, Now);
        series.CompleteAttempt(_challenger, gameNumber, challengerAttempt.Id, AttemptResult.OfScore((int)challengerScore), Now);
        series.CompleteAttempt(_opponent, gameNumber, opponentAttempt.Id, AttemptResult.OfScore((int)opponentScore), Now);
    }
}
