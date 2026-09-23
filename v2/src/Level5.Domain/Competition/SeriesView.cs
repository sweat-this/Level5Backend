using Level5.Domain.Ids;

namespace Level5.Domain.Competition;

/// <summary>A participant-safe, viewer-specific projection of a <see cref="VersusSeries"/>.</summary>
public sealed record SeriesView(
    VersusSeriesId Id,
    PlayerId ChallengerId,
    PlayerId OpponentId,
    SeriesStatus Status,
    int TotalGames,
    int GamesToWin,
    int CurrentGameNumber,
    long Revision,
    PlayerId? WinnerId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    FrozenRules Rules,
    IReadOnlyList<GameRoundView> Games);

/// <summary>
/// One game's attempts from the viewer's perspective. <see cref="AttemptView.Result"/> on
/// <see cref="OpponentAttempt"/> is null until both attempts in the round are complete -
/// enforced here, in the domain, rather than left to API/UI code to hide.
/// </summary>
public sealed record GameRoundView(int GameNumber, AttemptView? YourAttempt, AttemptView? OpponentAttempt);

public sealed record AttemptView(AttemptId Id, AttemptStatus Status, IReadOnlyDictionary<ResultMetric, double>? Result);
