using System.Text.Json;
using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Application.Competition;
using Level5.Domain.Competition;
using Level5.Domain.Ids;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.Persistence.Repositories;

public sealed class VersusSeriesStore(Level5V2DbContext db) : IVersusSeriesStore
{
    public async Task<VersusSeries?> FindByIdAsync(VersusSeriesId id, CancellationToken cancellationToken)
    {
        var row = await db.VersusSeries.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id.Value, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task AddAsync(VersusSeries series, Guid? clientRequestId, CancellationToken cancellationToken)
    {
        var row = ToRow(series);
        row.ClientRequestId = clientRequestId;
        db.VersusSeries.Add(row);

        try
        {
            await db.SaveChangesTranslatingConflictsAsync(cancellationToken);
        }
        catch
        {
            // Whatever failed the insert - a translated ConflictException or anything else (a
            // retry-budget exhaustion, say) - the row never committed. Stop tracking it so a
            // later save on this same scoped DbContext can't retry-insert the rejected row.
            db.Entry(row).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<VersusSeries?> FindByIdempotencyKeyAsync(PlayerId challengerId, Guid clientRequestId, CancellationToken cancellationToken)
    {
        var row = await db.VersusSeries.AsNoTracking()
            .SingleOrDefaultAsync(r => r.ChallengerId == challengerId.Value && r.ClientRequestId == clientRequestId, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public Task<PagedResult<SeriesSummary>> ListIncomingChallengeSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken)
        => PageByCreatedAtAsync(
            db.VersusSeries.AsNoTracking().Where(r => r.OpponentId == playerId.Value && r.Status == nameof(SeriesStatus.PendingAcceptance)),
            limit, cursor, SeriesListPaging.IncomingScope, cancellationToken);

    public Task<PagedResult<SeriesSummary>> ListOutgoingChallengeSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken)
        => PageByCreatedAtAsync(
            db.VersusSeries.AsNoTracking().Where(r => r.ChallengerId == playerId.Value && r.Status == nameof(SeriesStatus.PendingAcceptance)),
            limit, cursor, SeriesListPaging.OutgoingScope, cancellationToken);

    public Task<PagedResult<SeriesSummary>> ListActiveSeriesSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken)
        => PageByCreatedAtAsync(
            db.VersusSeries.AsNoTracking().Where(r => (r.ChallengerId == playerId.Value || r.OpponentId == playerId.Value) && r.Status == nameof(SeriesStatus.Active)),
            limit, cursor, SeriesListPaging.ActiveScope, cancellationToken);

    public Task<PagedResult<SeriesSummary>> ListCompletedSeriesSummariesAsync(PlayerId playerId, int? limit, string? cursor, CancellationToken cancellationToken)
        => PageByCompletedAtAsync(
            db.VersusSeries.AsNoTracking().Where(r => (r.ChallengerId == playerId.Value || r.OpponentId == playerId.Value) && r.Status == nameof(SeriesStatus.Completed)),
            limit, cursor, SeriesListPaging.CompletedScope, cancellationToken);

    /// <summary>Intermediate EF projection for a summary row - carries the actual creation time (for the DTO) separately from the keyset sort key, which differs between queries (see <see cref="PageByCompletedAtAsync"/>).</summary>
    private sealed record SummaryRow(
        Guid Id, Guid ChallengerId, Guid OpponentId, string Status, int CurrentGameNumber, int TotalGames, long Revision,
        DateTimeOffset CreatedAt, DateTimeOffset SortKey);

    /// <summary>
    /// Keyset-paginates by (CreatedAt, Id) descending and projects straight from relational
    /// columns to <see cref="SeriesSummary"/> - the generated SQL never selects <c>StateJson</c>,
    /// so a malformed or unsupported-schema-version document on any row (in or out of this page)
    /// cannot fail the query (issue #21). Used by incoming/outgoing/active, which have no
    /// completion time to order by.
    /// </summary>
    private static async Task<PagedResult<SeriesSummary>> PageByCreatedAtAsync(
        IQueryable<VersusSeriesRow> query, int? limit, string? cursor, string scope, CancellationToken cancellationToken)
    {
        var resolvedLimit = SeriesListPaging.ResolveLimit(limit);

        if (cursor is not null)
        {
            var (sortKey, id) = KeysetCursor.Decode(scope, cursor);
            query = query.Where(r => r.CreatedAt < sortKey || (r.CreatedAt == sortKey && r.Id < id));
        }

        var rows = await query
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Take(resolvedLimit + 1)
            .Select(r => new SummaryRow(r.Id, r.ChallengerId, r.OpponentId, r.Status, r.CurrentGameNumber, r.TotalGames, r.Revision, r.CreatedAt, r.CreatedAt))
            .ToListAsync(cancellationToken);

        return ToPage(rows, resolvedLimit, scope);
    }

    /// <summary>
    /// Same relational-only projection as <see cref="PageByCreatedAtAsync"/>, but keyset-paginated
    /// by (CompletedAt, Id) descending (issue #21's preferred completed-list ordering). Used only
    /// for the completed-series query, where every row is expected to already carry a
    /// <c>CompletedAt</c> - the <c>?? CreatedAt</c> fallback is defensive, not a normal path, and
    /// exists only to keep the ordering total if that invariant is ever violated.
    /// </summary>
    private static async Task<PagedResult<SeriesSummary>> PageByCompletedAtAsync(
        IQueryable<VersusSeriesRow> query, int? limit, string? cursor, string scope, CancellationToken cancellationToken)
    {
        var resolvedLimit = SeriesListPaging.ResolveLimit(limit);

        if (cursor is not null)
        {
            var (sortKey, id) = KeysetCursor.Decode(scope, cursor);
            query = query.Where(r => (r.CompletedAt ?? r.CreatedAt) < sortKey || ((r.CompletedAt ?? r.CreatedAt) == sortKey && r.Id < id));
        }

        var rows = await query
            .OrderByDescending(r => r.CompletedAt ?? r.CreatedAt).ThenByDescending(r => r.Id)
            .Take(resolvedLimit + 1)
            .Select(r => new SummaryRow(r.Id, r.ChallengerId, r.OpponentId, r.Status, r.CurrentGameNumber, r.TotalGames, r.Revision, r.CreatedAt, r.CompletedAt ?? r.CreatedAt))
            .ToListAsync(cancellationToken);

        return ToPage(rows, resolvedLimit, scope);
    }

    private static PagedResult<SeriesSummary> ToPage(List<SummaryRow> rows, int resolvedLimit, string scope)
    {
        var hasMore = rows.Count > resolvedLimit;
        var page = hasMore ? rows.Take(resolvedLimit).ToList() : rows;

        var items = page.Select(r => new SeriesSummary(
            new VersusSeriesId(r.Id), new PlayerId(r.ChallengerId), new PlayerId(r.OpponentId),
            Enum.Parse<SeriesStatus>(r.Status), r.CurrentGameNumber, r.TotalGames, r.Revision, r.CreatedAt)).ToList();

        var nextCursor = hasMore ? KeysetCursor.Encode(scope, page[^1].SortKey, page[^1].Id) : null;
        return new PagedResult<SeriesSummary>(items, nextCursor);
    }

    public async Task<bool> TrySaveAsync(VersusSeries series, long expectedRevision, CancellationToken cancellationToken)
    {
        var stateJson = SerializeState(series);

        // Conditional UPDATE ... WHERE id = @id AND revision = @expectedRevision, executed
        // directly against the database rather than through change tracking, so the affected-row
        // count is the concurrency check: 1 means our write won, 0 means someone else's write
        // (or a prior retry of this same write) already moved the revision forward.
        var affected = await db.VersusSeries
            .Where(r => r.Id == series.Id.Value && r.Revision == expectedRevision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Status, series.Status.ToString())
                .SetProperty(r => r.CurrentGameNumber, series.CurrentGameNumber)
                .SetProperty(r => r.WinnerId, series.WinnerId != null ? series.WinnerId.Value.Value : (Guid?)null)
                .SetProperty(r => r.Revision, series.Revision)
                .SetProperty(r => r.UpdatedAt, series.UpdatedAt)
                .SetProperty(r => r.CompletedAt, series.CompletedAt)
                .SetProperty(r => r.StateJson, stateJson),
                cancellationToken);

        return affected == 1;
    }

    private static VersusSeriesRow ToRow(VersusSeries series) => new()
    {
        Id = series.Id.Value,
        ChallengerId = series.ChallengerId.Value,
        OpponentId = series.OpponentId.Value,
        Status = series.Status.ToString(),
        TotalGames = series.Format.TotalGames,
        CurrentGameNumber = series.CurrentGameNumber,
        WinnerId = series.WinnerId?.Value,
        Revision = series.Revision,
        CreatedAt = series.CreatedAt,
        UpdatedAt = series.UpdatedAt,
        CompletedAt = series.CompletedAt,
        StateJson = SerializeState(series)
    };

    private static VersusSeries ToDomain(VersusSeriesRow row)
    {
        var state = DeserializeState(row.Id, row.StateJson);

        var rules = ToFrozenRules(row.Id, state.Rules);
        var rounds = state.Rounds.Select(r => GameRound.Rehydrate(
            r.GameNumber,
            ToAttempt(row.Id, r.ChallengerAttempt),
            ToAttempt(row.Id, r.OpponentAttempt)));

        return VersusSeries.Rehydrate(
            new VersusSeriesId(row.Id),
            new PlayerId(row.ChallengerId),
            new PlayerId(row.OpponentId),
            SeriesFormat.Rehydrate(row.TotalGames),
            rules,
            Enum.Parse<SeriesStatus>(row.Status),
            row.CurrentGameNumber,
            row.WinnerId.HasValue ? new PlayerId(row.WinnerId.Value) : null,
            row.Revision,
            row.CreatedAt,
            row.UpdatedAt,
            row.CompletedAt,
            rounds);
    }

    /// <summary>
    /// Reads <see cref="SeriesSchemaVersionEnvelope.SchemaVersion"/> before committing to a full,
    /// version-specific deserialize, so an unsupported version fails with a clear, explicit
    /// exception rather than a confusing JSON-shape error (a version-1 row has no "rules" field
    /// at all, which <see cref="VersusSeriesStateJson.Rules"/> being <c>required</c> would
    /// otherwise surface as a generic deserialization failure).
    /// </summary>
    private static VersusSeriesStateJson DeserializeState(Guid seriesId, string stateJson)
    {
        var envelope = JsonSerializer.Deserialize<SeriesSchemaVersionEnvelope>(stateJson)
            ?? throw new UnsupportedSeriesSchemaVersionException($"Series {seriesId} has an empty or unparseable persisted state document.");

        if (envelope.SchemaVersion != VersusSeriesStateJson.CurrentSchemaVersion)
        {
            throw new UnsupportedSeriesSchemaVersionException(
                $"Series {seriesId} was persisted with schema version {envelope.SchemaVersion}, but this build only " +
                $"supports version {VersusSeriesStateJson.CurrentSchemaVersion}. Schema v1 rows predate the Competition " +
                "Protocol V1 frozen-rules format (issue #9) and cannot be safely reconstructed - a v1 row has no " +
                "RulesetId/ComparisonKeys/InformationPolicy at all, so there is nothing to fabricate frozen rules from. " +
                "V2 has not shipped to any environment with real user data, so the resolution is to recreate the " +
                "affected series rather than migrate it in place.");
        }

        return JsonSerializer.Deserialize<VersusSeriesStateJson>(stateJson)
            ?? throw new UnsupportedSeriesSchemaVersionException($"Series {seriesId} has an empty or unparseable persisted state document.");
    }

    private static FrozenRules ToFrozenRules(Guid seriesId, FrozenRulesJson json) => FrozenRules.Create(
        json.CompetitionProtocolVersion,
        json.RulesetId,
        json.RulesetVersion,
        json.MinimumCompatibleVersion,
        json.ModeId,
        ParseEnum<InformationPolicy>(seriesId, json.InformationPolicy, "InformationPolicy"),
        json.AlternatesFirstAttempt,
        [.. json.ComparisonKeys.Select(k => ToComparisonKey(seriesId, k))]);

    private static ComparisonKey ToComparisonKey(Guid seriesId, ComparisonKeyJson json) => new(
        ParseEnum<ResultMetric>(seriesId, json.Metric, "comparison key metric"),
        ParseEnum<MetricDirection>(seriesId, json.Direction, "comparison key direction"));

    private static TEnum ParseEnum<TEnum>(Guid seriesId, string value, string fieldName) where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(value, out var parsed))
        {
            throw new CorruptSeriesStateException(
                $"Series {seriesId} has a persisted {fieldName} value '{value}' that is not a recognized {typeof(TEnum).Name}.");
        }

        return parsed;
    }

    private static GameAttempt? ToAttempt(Guid seriesId, AttemptJson? json)
    {
        if (json is null)
        {
            return null;
        }

        return GameAttempt.Rehydrate(
            new AttemptId(json.Id),
            new PlayerId(json.PlayerId),
            Enum.Parse<AttemptStatus>(json.Status),
            ToAttemptResult(seriesId, json.Result),
            json.StartedAt,
            json.CompletedAt);
    }

    private static AttemptResult? ToAttemptResult(Guid seriesId, Dictionary<string, double>? json)
    {
        if (json is null || json.Count == 0)
        {
            return null;
        }

        var metrics = new Dictionary<ResultMetric, double>(json.Count);
        foreach (var (key, value) in json)
        {
            metrics[ParseEnum<ResultMetric>(seriesId, key, "attempt result metric")] = value;
        }

        return AttemptResult.Of(metrics);
    }

    private static string SerializeState(VersusSeries series)
    {
        var state = new VersusSeriesStateJson
        {
            SchemaVersion = VersusSeriesStateJson.CurrentSchemaVersion,
            Rules = ToFrozenRulesJson(series.Rules),
            Rounds = [.. series.Rounds.Select(r => new GameRoundJson
            {
                GameNumber = r.GameNumber,
                ChallengerAttempt = ToAttemptJson(r.ChallengerAttempt),
                OpponentAttempt = ToAttemptJson(r.OpponentAttempt)
            })]
        };

        return JsonSerializer.Serialize(state);
    }

    private static FrozenRulesJson ToFrozenRulesJson(FrozenRules rules) => new()
    {
        CompetitionProtocolVersion = rules.CompetitionProtocolVersion,
        RulesetId = rules.RulesetId,
        RulesetVersion = rules.RulesetVersion,
        MinimumCompatibleVersion = rules.MinimumCompatibleVersion,
        ModeId = rules.ModeId,
        InformationPolicy = rules.InformationPolicy.ToString(),
        AlternatesFirstAttempt = rules.AlternatesFirstAttempt,
        ComparisonKeys = [.. rules.ComparisonKeys.Select(k => new ComparisonKeyJson { Metric = k.Metric.ToString(), Direction = k.Direction.ToString() })]
    };

    private static AttemptJson? ToAttemptJson(GameAttempt? attempt)
    {
        if (attempt is null)
        {
            return null;
        }

        return new AttemptJson
        {
            Id = attempt.Id.Value,
            PlayerId = attempt.PlayerId.Value,
            Status = attempt.Status.ToString(),
            Result = attempt.Result?.Metrics.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            StartedAt = attempt.StartedAt,
            CompletedAt = attempt.CompletedAt
        };
    }
}
