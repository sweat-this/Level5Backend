using Level5.Application.Abstractions;

namespace Level5.Application.Tests.Fakes;

/// <summary>Records the last spec it was called with and returns a fixed row set - lets use-case tests assert on what GetLeaderboardUseCase actually asked the query port for, without a real database.</summary>
public sealed class FakeLeaderboardQuery : ILeaderboardQuery
{
    public LeaderboardQuerySpec? LastSpec { get; private set; }
    public IReadOnlyList<LeaderboardRow> RowsToReturn { get; set; } = [];

    public Task<IReadOnlyList<LeaderboardRow>> ExecuteAsync(LeaderboardQuerySpec spec, CancellationToken cancellationToken)
    {
        LastSpec = spec;
        return Task.FromResult(RowsToReturn);
    }
}
