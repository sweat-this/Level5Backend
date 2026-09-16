using Level5.Domain.Competition;
using Level5.Domain.Ids;
using Xunit;

namespace Level5.Domain.Tests.Competition;

public class VersusSeriesTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly PlayerId _challenger = PlayerId.New();
    private readonly PlayerId _opponent = PlayerId.New();

    private VersusSeries CreateBestOf(int totalGames)
        => VersusSeries.CreateChallenge(_challenger, _opponent, SeriesFormat.BestOf(totalGames), Now);

    [Fact]
    public void CreateChallenge_starts_pending_acceptance_with_revision_zero()
    {
        var series = CreateBestOf(3);

        Assert.Equal(SeriesStatus.PendingAcceptance, series.Status);
        Assert.Equal(0, series.Revision);
        Assert.Equal(1, series.CurrentGameNumber);
    }

    [Fact]
    public void CreateChallenge_against_self_is_rejected()
    {
        Assert.Throws<InvalidChallengeException>(() =>
            VersusSeries.CreateChallenge(_challenger, _challenger, SeriesFormat.BestOf(3), Now));
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
    public void Accept_after_already_accepted_is_an_illegal_transition()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);

        Assert.Throws<IllegalSeriesTransitionException>(() => series.Accept(_opponent, Now));
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
        series.StartAttempt(_challenger, 1, Now);
        series.StartAttempt(_opponent, 1, Now);
        series.CompleteAttempt(_challenger, 1, Score.Of(50), Now);
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

        Assert.Throws<AttemptNotStartedException>(() => series.CompleteAttempt(_challenger, 1, Score.Of(10), Now));
    }

    [Fact]
    public void CompleteAttempt_twice_is_idempotent_and_keeps_the_original_result()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);
        series.StartAttempt(_challenger, 1, Now);

        var first = series.CompleteAttempt(_challenger, 1, Score.Of(50), Now);
        var revisionAfterFirstComplete = series.Revision;
        var second = series.CompleteAttempt(_challenger, 1, Score.Of(999), Now);

        Assert.Equal(50, first.Result!.Value.Value);
        Assert.Equal(50, second.Result!.Value.Value);
        Assert.Equal(revisionAfterFirstComplete, series.Revision);
    }

    [Fact]
    public void Round_does_not_resolve_until_both_participants_complete()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);
        series.StartAttempt(_challenger, 1, Now);
        series.StartAttempt(_opponent, 1, Now);

        series.CompleteAttempt(_challenger, 1, Score.Of(50), Now);

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
    public void Exhausting_every_game_on_ties_completes_the_series_as_a_draw()
    {
        var series = CreateBestOf(1);
        series.Accept(_opponent, Now);

        series.StartAttempt(_challenger, 1, Now);
        series.StartAttempt(_opponent, 1, Now);
        series.CompleteAttempt(_challenger, 1, Score.Of(10), Now);
        series.CompleteAttempt(_opponent, 1, Score.Of(10), Now);

        Assert.Equal(SeriesStatus.Completed, series.Status);
        Assert.Null(series.WinnerId);
    }

    [Fact]
    public void ToView_seals_opponents_score_until_the_round_resolves()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);
        series.StartAttempt(_challenger, 1, Now);
        series.StartAttempt(_opponent, 1, Now);
        series.CompleteAttempt(_challenger, 1, Score.Of(77), Now);

        var opponentView = series.ToView(_opponent);
        var round = opponentView.Games.Single(g => g.GameNumber == 1);

        // The opponent (viewer) has not completed their attempt, so the round is unresolved: the
        // challenger's attempt shows as completed (state is not secret) but its score must not leak.
        Assert.Equal(Domain.Competition.AttemptStatus.Completed, round.OpponentAttempt!.Status);
        Assert.Null(round.OpponentAttempt.Score);
    }

    [Fact]
    public void ToView_reveals_both_scores_once_the_round_resolves()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);
        series.StartAttempt(_challenger, 1, Now);
        series.StartAttempt(_opponent, 1, Now);
        series.CompleteAttempt(_challenger, 1, Score.Of(77), Now);
        series.CompleteAttempt(_opponent, 1, Score.Of(60), Now);

        var opponentView = series.ToView(_opponent);
        var round = opponentView.Games.Single(g => g.GameNumber == 1);

        Assert.Equal(77, round.OpponentAttempt!.Score);
        Assert.Equal(60, round.YourAttempt!.Score);
    }

    [Fact]
    public void CompleteAttempt_retry_after_that_completion_finished_the_series_still_returns_the_result()
    {
        var series = CreateBestOf(3);
        series.Accept(_opponent, Now);
        PlayChallengerWin(series, gameNumber: 1);

        series.StartAttempt(_challenger, 2, Now);
        series.StartAttempt(_opponent, 2, Now);
        series.CompleteAttempt(_challenger, 2, Score.Of(100), Now);
        series.CompleteAttempt(_opponent, 2, Score.Of(10), Now);

        Assert.Equal(SeriesStatus.Completed, series.Status);

        // The client's HTTP response for that last completion never arrived, so it retries the
        // exact same call. The series is no longer Active, but the retry must still succeed and
        // return the original result rather than fail because the series has moved on.
        var retried = series.CompleteAttempt(_opponent, 2, Score.Of(10), Now);

        Assert.Equal(10, retried.Result!.Value.Value);
    }

    [Fact]
    public void StartAttempt_retry_after_the_series_completed_still_returns_the_attempt()
    {
        var series = CreateBestOf(1);
        series.Accept(_opponent, Now);
        var started = series.StartAttempt(_challenger, 1, Now);
        series.StartAttempt(_opponent, 1, Now);
        series.CompleteAttempt(_challenger, 1, Score.Of(100), Now);
        series.CompleteAttempt(_opponent, 1, Score.Of(10), Now);

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

    private void PlayChallengerWin(VersusSeries series, int gameNumber)
    {
        series.StartAttempt(_challenger, gameNumber, Now);
        series.StartAttempt(_opponent, gameNumber, Now);
        series.CompleteAttempt(_challenger, gameNumber, Score.Of(100), Now);
        series.CompleteAttempt(_opponent, gameNumber, Score.Of(10), Now);
    }
}
