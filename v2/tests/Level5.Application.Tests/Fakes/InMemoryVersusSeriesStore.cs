using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Competition;
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
    private readonly Dictionary<Guid, Guid> _clientRequestIds = [];

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

    public Task AddAsync(VersusSeries series, Guid? clientRequestId, CancellationToken cancellationToken)
    {
        if (clientRequestId is { } key)
        {
            var duplicateKey = _clientRequestIds.Any(kv => kv.Value == key && _rows[kv.Key].ChallengerId == series.ChallengerId);
            if (duplicateKey)
            {
                throw new ConflictException("The request conflicts with existing data. Please retry.");
            }

            _clientRequestIds[series.Id.Value] = key;
        }

        _rows[series.Id.Value] = series;
        _storedRevisions[series.Id.Value] = series.Revision;
        return Task.CompletedTask;
    }

    public Task<VersusSeries?> FindByIdempotencyKeyAsync(PlayerId challengerId, Guid clientRequestId, CancellationToken cancellationToken)
    {
        var match = _clientRequestIds
            .Where(kv => kv.Value == clientRequestId && _rows[kv.Key].ChallengerId == challengerId)
            .Select(kv => _rows[kv.Key])
            .SingleOrDefault();
        return Task.FromResult(match is null ? null : Clone(match));
    }

    public Task<PagedResult<SeriesSummary>> ListIncomingChallengeSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken)
        => Task.FromResult(Page(
            _rows.Values.Where(s => s.OpponentId == playerId && s.Status == SeriesStatus.PendingAcceptance),
            s => s.CreatedAt, limit, cursor, SeriesListPaging.IncomingScope));

    public Task<PagedResult<SeriesSummary>> ListOutgoingChallengeSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken)
        => Task.FromResult(Page(
            _rows.Values.Where(s => s.ChallengerId == playerId && s.Status == SeriesStatus.PendingAcceptance),
            s => s.CreatedAt, limit, cursor, SeriesListPaging.OutgoingScope));

    public Task<PagedResult<SeriesSummary>> ListActiveSeriesSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken)
        => Task.FromResult(Page(
            _rows.Values.Where(s => (s.ChallengerId == playerId || s.OpponentId == playerId) && s.Status == SeriesStatus.Active),
            s => s.CreatedAt, limit, cursor, SeriesListPaging.ActiveScope));

    public Task<PagedResult<SeriesSummary>> ListCompletedSeriesSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken)
        => Task.FromResult(Page(
            _rows.Values.Where(s => (s.ChallengerId == playerId || s.OpponentId == playerId) && s.Status == SeriesStatus.Completed),
            s => s.CompletedAt ?? s.CreatedAt, limit, cursor, SeriesListPaging.CompletedScope));

    private static readonly SeriesStatus[] TerminalStatuses =
        [SeriesStatus.Completed, SeriesStatus.Declined, SeriesStatus.Cancelled, SeriesStatus.Expired];

    public Task<PagedResult<SeriesSummary>> ListTerminalHistorySummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken)
        => Task.FromResult(Page(
            _rows.Values.Where(s => (s.ChallengerId == playerId || s.OpponentId == playerId) && TerminalStatuses.Contains(s.Status)),
            s => s.CompletedAt ?? s.CreatedAt, limit, cursor, SeriesListPaging.HistoryScope));

    public Task<IReadOnlyList<VersusSeriesId>> FindStalePendingChallengeIdsAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken)
    {
        IReadOnlyList<VersusSeriesId> ids =
        [
            .. _rows.Values
                .Where(s => s.Status == SeriesStatus.PendingAcceptance && s.CreatedAt < cutoff)
                .OrderBy(s => s.CreatedAt)
                .Take(batchSize)
                .Select(s => s.Id)
        ];
        return Task.FromResult(ids);
    }

    /// <summary>
    /// Mirrors <c>VersusSeriesStore</c>'s keyset pagination (issue #21): descending by
    /// <paramref name="sortKey"/> then <c>Id</c>, with the same opaque, scope-tagged cursor format
    /// (<see cref="KeysetCursor"/>), so application-layer tests exercise the real
    /// pass-through/boundary/cross-scope-rejection semantics rather than a simplified stand-in.
    /// </summary>
    private static PagedResult<SeriesSummary> Page(
        IEnumerable<VersusSeries> source, Func<VersusSeries, DateTimeOffset> sortKey, int? limit, string? cursor, string scope)
    {
        var resolvedLimit = SeriesListPaging.ResolveLimit(limit);
        var ordered = source.OrderByDescending(sortKey).ThenByDescending(s => s.Id.Value).AsEnumerable();

        if (cursor is not null)
        {
            var (cursorSortKey, cursorId) = KeysetCursor.Decode(scope, cursor);
            ordered = ordered.Where(s => sortKey(s) < cursorSortKey || (sortKey(s) == cursorSortKey && s.Id.Value < cursorId));
        }

        var rows = ordered.Take(resolvedLimit + 1).ToList();
        var hasMore = rows.Count > resolvedLimit;
        var page = hasMore ? rows.Take(resolvedLimit).ToList() : rows;

        var items = page.Select(ToSummary).ToList();
        var nextCursor = hasMore ? KeysetCursor.Encode(scope, sortKey(page[^1]), page[^1].Id.Value) : null;
        return new PagedResult<SeriesSummary>(items, nextCursor);
    }

    private static SeriesSummary ToSummary(VersusSeries s)
        => new(s.Id, s.ChallengerId, s.OpponentId, s.Status, s.CurrentGameNumber, s.Format.TotalGames, s.Revision, s.CreatedAt);

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
