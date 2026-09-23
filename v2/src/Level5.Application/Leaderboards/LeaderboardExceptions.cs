using Level5.Application.Common;

namespace Level5.Application.Leaderboards;

/// <summary>
/// Thrown by <see cref="GetLeaderboardUseCase"/> when the requested mode id has no
/// <see cref="Level5.Domain.Leaderboards.LeaderboardPolicy"/> - a deterministic error, never a
/// silently empty board (a client cannot distinguish "no policy" from "no rows yet" otherwise).
/// </summary>
public sealed class UnsupportedLeaderboardModeException : AppException
{
    public override string Code => "unsupported_leaderboard_mode";

    public UnsupportedLeaderboardModeException(string message) : base(message)
    {
    }
}

/// <summary>
/// Thrown by <see cref="Level5.Application.Results.SubmitMatchResultUseCase"/> when a match result
/// is submitted for a mode with a <see cref="Level5.Domain.Leaderboards.LeaderboardPolicy"/>, but
/// the submitted metrics do not include that policy's required ranking metric - a supported
/// leaderboard mode's results must always carry the value that would rank them.
/// </summary>
public sealed class RequiredLeaderboardMetricMissingException : AppException
{
    public override string Code => "required_leaderboard_metric_missing";

    public RequiredLeaderboardMetricMissingException(string message) : base(message)
    {
    }
}
