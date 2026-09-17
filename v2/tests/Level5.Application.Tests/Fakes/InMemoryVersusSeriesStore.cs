using Level5.Application.Abstractions;
using Level5.Domain.Competition;
using Level5.Domain.Ids;

namespace Level5.Application.Tests.Fakes;

/// <summary>
/// Mimics the real store's revision-gated write: a "row" is (series snapshot, revision), and
/// TrySaveAsync only applies if the caller's expected revision still matches. Good enough to
/// exercise the application layer's concurrency-conflict handling without a real database.
/// </summary>
public sealed class InMemoryVersusSeriesStore : IVersusSeriesStore
{
    private readonly Dictionary<Guid, long> _storedRevisions = [];
    private readonly Dictionary<Guid, VersusSeries> _rows = [];

    public Task<VersusSeries?> FindByIdAsync(VersusSeriesId id, CancellationToken cancellationToken)
    {
        var stored = _rows.GetValueOrDefault(id.Value);
        // A real store returns a freshly-materialized object on every read; this fake must too,
        // so two "concurrent" callers get independent snapshots instead of secretly sharing (and
        // mutating) the same in-memory instance, which would make concurrency tests meaningless.
        return Task.FromResult(stored is null ? null : Clone(stored));
    }

    private static VersusSeries Clone(VersusSeries source) => VersusSeries.Rehydrate(
        source.Id, source.ChallengerId, source.OpponentId, SeriesFormat.Rehydrate(source.Format.TotalGames), source.Rules,
        source.Status, source.CurrentGameNumber, source.WinnerId, source.Revision,
        source.CreatedAt, source.UpdatedAt, source.CompletedAt,
        source.Rounds.Select(r => GameRound.Rehydrate(r.GameNumber, CloneAttempt(r.ChallengerAttempt), CloneAttempt(r.OpponentAttempt))));

    private static GameAttempt? CloneAttempt(GameAttempt? attempt) => attempt is null
        ? null
        : GameAttempt.Rehydrate(attempt.Id, attempt.PlayerId, attempt.Status, attempt.Result, attempt.StartedAt, attempt.CompletedAt);

    public Task AddAsync(VersusSeries series, CancellationToken cancellationToken)
    {
        _rows[series.Id.Value] = series;
        _storedRevisions[series.Id.Value] = series.Revision;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VersusSeries>> ListIncomingChallengesAsync(PlayerId playerId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<VersusSeries>>(
            [.. _rows.Values.Where(s => s.OpponentId == playerId && s.Status == SeriesStatus.PendingAcceptance)]);

    public Task<IReadOnlyList<VersusSeries>> ListOutgoingChallengesAsync(PlayerId playerId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<VersusSeries>>(
            [.. _rows.Values.Where(s => s.ChallengerId == playerId && s.Status == SeriesStatus.PendingAcceptance)]);

    public Task<IReadOnlyList<VersusSeries>> ListActiveSeriesAsync(PlayerId playerId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<VersusSeries>>(
            [.. _rows.Values.Where(s => (s.ChallengerId == playerId || s.OpponentId == playerId) && s.Status == SeriesStatus.Active)]);

    public Task<bool> TrySaveAsync(VersusSeries series, long expectedRevision, CancellationToken cancellationToken)
    {
        if (_storedRevisions[series.Id.Value] != expectedRevision)
        {
            return Task.FromResult(false);
        }

        _rows[series.Id.Value] = series;
        _storedRevisions[series.Id.Value] = series.Revision;
        return Task.FromResult(true);
    }
}
