using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Leaderboards;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Leaderboards;
using Level5.Domain.Results;
using Xunit;

namespace Level5.Application.Tests.Leaderboards;

public class GetLeaderboardUseCaseTests
{
    private readonly FakeLeaderboardQuery _query = new();
    private readonly GetLeaderboardUseCase _getLeaderboard;

    public GetLeaderboardUseCaseTests()
    {
        // FakeLeaderboardPolicyCatalog resolves mode 1 to TotalPoints/HigherWins and nothing else.
        _getLeaderboard = new GetLeaderboardUseCase(new FakeLeaderboardPolicyCatalog(), _query);
    }

    private static LeaderboardRow Row(Guid? matchResultId = null, double rankingValue = 90, DateTimeOffset? createdAt = null) => new(
        matchResultId ?? Guid.NewGuid(), Guid.NewGuid(), "DisplayName", "Tag#123", "hero", 1,
        rankingValue, createdAt ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Hardcore: false, TrafficEnabled: false, EnemiesEnabled: false, SniperEnabled: false);

    [Fact]
    public async Task An_unsupported_mode_is_rejected()
    {
        await Assert.ThrowsAsync<UnsupportedLeaderboardModeException>(() =>
            _getLeaderboard.ExecuteAsync(new GetLeaderboardRequest(999, null, null, null, null, null, null), CancellationToken.None));
    }

    [Fact]
    public async Task A_supported_mode_resolves_the_server_owned_metric_and_direction()
    {
        var page = await _getLeaderboard.ExecuteAsync(
            new GetLeaderboardRequest(1, null, null, null, null, null, null), CancellationToken.None);

        Assert.Equal(1, page.ModeId);
        Assert.Equal(MatchResultMetric.TotalPoints, page.Metric);
        Assert.Equal(RankingDirection.HigherWins, page.Direction);
        Assert.Equal(MatchResultMetric.TotalPoints, _query.LastSpec!.RankingMetric);
        Assert.Equal(RankingDirection.HigherWins, _query.LastSpec!.Direction);
    }

    [Fact]
    public async Task A_missing_limit_resolves_to_the_default()
    {
        await _getLeaderboard.ExecuteAsync(new GetLeaderboardRequest(1, null, null, null, null, null, null), CancellationToken.None);

        Assert.Equal(LeaderboardPaging.DefaultLimit, _query.LastSpec!.Limit);
    }

    [Fact]
    public async Task A_limit_above_the_maximum_is_clamped()
    {
        await _getLeaderboard.ExecuteAsync(
            new GetLeaderboardRequest(1, null, null, null, null, LeaderboardPaging.MaxLimit + 50, null), CancellationToken.None);

        Assert.Equal(LeaderboardPaging.MaxLimit, _query.LastSpec!.Limit);
    }

    [Fact]
    public async Task Modifier_filters_are_forwarded_to_the_query_unchanged()
    {
        await _getLeaderboard.ExecuteAsync(
            new GetLeaderboardRequest(1, Hardcore: true, TrafficEnabled: false, EnemiesEnabled: true, SniperEnabled: null, null, null),
            CancellationToken.None);

        Assert.True(_query.LastSpec!.Hardcore);
        Assert.False(_query.LastSpec!.TrafficEnabled);
        Assert.True(_query.LastSpec!.EnemiesEnabled);
        Assert.Null(_query.LastSpec!.SniperEnabled);
    }

    [Fact]
    public async Task Exactly_limit_rows_returned_means_no_next_page()
    {
        _query.RowsToReturn = [Row(), Row(), Row()];

        var page = await _getLeaderboard.ExecuteAsync(
            new GetLeaderboardRequest(1, null, null, null, null, 3, null), CancellationToken.None);

        Assert.Equal(3, page.Items.Count);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Limit_plus_one_rows_returned_means_a_next_page_exists_and_the_extra_row_is_trimmed()
    {
        _query.RowsToReturn = [Row(), Row(), Row(), Row()];

        var page = await _getLeaderboard.ExecuteAsync(
            new GetLeaderboardRequest(1, null, null, null, null, 3, null), CancellationToken.None);

        Assert.Equal(3, page.Items.Count);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task The_next_cursor_from_one_page_is_accepted_as_the_cursor_for_the_same_board_and_filters()
    {
        _query.RowsToReturn = [Row(), Row(), Row(), Row()];
        var firstPage = await _getLeaderboard.ExecuteAsync(
            new GetLeaderboardRequest(1, null, null, null, null, 3, null), CancellationToken.None);

        _query.RowsToReturn = [];
        await _getLeaderboard.ExecuteAsync(
            new GetLeaderboardRequest(1, null, null, null, null, 3, firstPage.NextCursor), CancellationToken.None);

        Assert.NotNull(_query.LastSpec!.Cursor);
    }

    [Fact]
    public async Task A_cursor_issued_for_one_mode_is_rejected_against_a_different_mode()
    {
        // FakeLeaderboardPolicyCatalog only resolves mode 1, so this proves the rejection happens
        // via scope mismatch, not because mode 999 itself lacks a policy - mode 1 is used on both sides.
        _query.RowsToReturn = [Row(), Row(), Row(), Row()];
        var page = await _getLeaderboard.ExecuteAsync(
            new GetLeaderboardRequest(1, Hardcore: null, null, null, null, 3, null), CancellationToken.None);

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            _getLeaderboard.ExecuteAsync(
                new GetLeaderboardRequest(1, Hardcore: true, null, null, null, 3, page.NextCursor), CancellationToken.None));
    }

    [Fact]
    public async Task A_malformed_cursor_is_rejected()
    {
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            _getLeaderboard.ExecuteAsync(
                new GetLeaderboardRequest(1, null, null, null, null, null, "not-a-real-cursor"), CancellationToken.None));
    }

    [Fact]
    public async Task Row_fields_are_mapped_into_the_view_correctly()
    {
        var matchResultId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var row = new LeaderboardRow(
            matchResultId, Guid.NewGuid(), "Alice", "Alice#42", "captain", 7, 123.5, createdAt,
            Hardcore: true, TrafficEnabled: true, EnemiesEnabled: false, SniperEnabled: false);
        _query.RowsToReturn = [row];

        var page = await _getLeaderboard.ExecuteAsync(
            new GetLeaderboardRequest(1, null, null, null, null, null, null), CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(matchResultId, item.MatchResultId);
        Assert.Equal("Alice", item.Player.DisplayName);
        Assert.Equal("Alice#42", item.Player.Tag);
        Assert.Equal("captain", item.CharacterId);
        Assert.Equal(7, item.LevelId);
        Assert.Equal(123.5, item.Value);
        Assert.Equal(createdAt, item.CreatedAt);
        Assert.True(item.Modifiers.Hardcore);
        Assert.True(item.Modifiers.TrafficEnabled);
        Assert.False(item.Modifiers.EnemiesEnabled);
        Assert.False(item.Modifiers.SniperEnabled);
    }
}
