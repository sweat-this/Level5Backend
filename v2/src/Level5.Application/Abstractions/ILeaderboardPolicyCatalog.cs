using Level5.Domain.Leaderboards;

namespace Level5.Application.Abstractions;

/// <summary>
/// Server-authoritative source of which <see cref="Level5.Domain.Results.MatchResult.ModeId"/>
/// values have a leaderboard, and how each one ranks. A client may name a mode id but never
/// dictates the ranking metric, sort direction, JSON field, or database field - this port resolves
/// those from the server's own catalog, and a mode id with no entry has no leaderboard (not an
/// empty one - see <see cref="Level5.Application.Leaderboards.UnsupportedLeaderboardModeException"/>).
/// </summary>
public interface ILeaderboardPolicyCatalog
{
    /// <summary>The policy for <paramref name="modeId"/>, or <c>null</c> if that mode has no leaderboard.</summary>
    LeaderboardPolicy? TryResolve(int modeId);
}
