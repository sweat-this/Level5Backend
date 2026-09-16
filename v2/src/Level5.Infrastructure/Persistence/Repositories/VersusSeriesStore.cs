using System.Text.Json;
using Level5.Application.Abstractions;
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

    public async Task AddAsync(VersusSeries series, CancellationToken cancellationToken)
    {
        db.VersusSeries.Add(ToRow(series));
        await db.SaveChangesTranslatingConflictsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<VersusSeries>> ListIncomingChallengesAsync(PlayerId playerId, CancellationToken cancellationToken)
    {
        var rows = await db.VersusSeries.AsNoTracking()
            .Where(r => r.OpponentId == playerId.Value && r.Status == nameof(SeriesStatus.PendingAcceptance))
            .ToListAsync(cancellationToken);
        return [.. rows.Select(ToDomain)];
    }

    public async Task<IReadOnlyList<VersusSeries>> ListOutgoingChallengesAsync(PlayerId playerId, CancellationToken cancellationToken)
    {
        var rows = await db.VersusSeries.AsNoTracking()
            .Where(r => r.ChallengerId == playerId.Value && r.Status == nameof(SeriesStatus.PendingAcceptance))
            .ToListAsync(cancellationToken);
        return [.. rows.Select(ToDomain)];
    }

    public async Task<IReadOnlyList<VersusSeries>> ListActiveSeriesAsync(PlayerId playerId, CancellationToken cancellationToken)
    {
        var rows = await db.VersusSeries.AsNoTracking()
            .Where(r => (r.ChallengerId == playerId.Value || r.OpponentId == playerId.Value) && r.Status == nameof(SeriesStatus.Active))
            .ToListAsync(cancellationToken);
        return [.. rows.Select(ToDomain)];
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
        var state = JsonSerializer.Deserialize<VersusSeriesStateJson>(row.StateJson) ?? new VersusSeriesStateJson();

        var rounds = state.Rounds.Select(r => GameRound.Rehydrate(
            r.GameNumber,
            ToAttempt(r.ChallengerAttempt),
            ToAttempt(r.OpponentAttempt)));

        return VersusSeries.Rehydrate(
            new VersusSeriesId(row.Id),
            new PlayerId(row.ChallengerId),
            new PlayerId(row.OpponentId),
            SeriesFormat.Rehydrate(row.TotalGames),
            Enum.Parse<SeriesStatus>(row.Status),
            row.CurrentGameNumber,
            row.WinnerId.HasValue ? new PlayerId(row.WinnerId.Value) : null,
            row.Revision,
            row.CreatedAt,
            row.UpdatedAt,
            row.CompletedAt,
            rounds);
    }

    private static GameAttempt? ToAttempt(AttemptJson? json)
    {
        if (json is null)
        {
            return null;
        }

        return GameAttempt.Rehydrate(
            new AttemptId(json.Id),
            new PlayerId(json.PlayerId),
            Enum.Parse<AttemptStatus>(json.Status),
            json.Score.HasValue ? Score.Of(json.Score.Value) : null,
            json.StartedAt,
            json.CompletedAt);
    }

    private static string SerializeState(VersusSeries series)
    {
        var state = new VersusSeriesStateJson
        {
            SchemaVersion = 1,
            Rounds = [.. series.Rounds.Select(r => new GameRoundJson
            {
                GameNumber = r.GameNumber,
                ChallengerAttempt = ToAttemptJson(r.ChallengerAttempt),
                OpponentAttempt = ToAttemptJson(r.OpponentAttempt)
            })]
        };

        return JsonSerializer.Serialize(state);
    }

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
            Score = attempt.Result?.Value,
            StartedAt = attempt.StartedAt,
            CompletedAt = attempt.CompletedAt
        };
    }
}
