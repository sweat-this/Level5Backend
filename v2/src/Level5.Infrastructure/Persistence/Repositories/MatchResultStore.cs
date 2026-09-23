using System.Text.Json;
using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Domain.Results;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.Persistence.Repositories;

public sealed class MatchResultStore(Level5V2DbContext db) : IMatchResultStore
{
    public async Task<MatchResult?> FindByClientResultIdAsync(PlayerId playerId, Guid clientResultId, CancellationToken cancellationToken)
    {
        var row = await db.MatchResults.AsNoTracking()
            .SingleOrDefaultAsync(r => r.PlayerId == playerId.Value && r.ClientResultId == clientResultId, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task AddAsync(MatchResult result, CancellationToken cancellationToken)
    {
        db.MatchResults.Add(ToRow(result));
        await db.SaveChangesTranslatingConflictsAsync(cancellationToken);
    }

    private static MatchResultRow ToRow(MatchResult result) => new()
    {
        Id = result.Id.Value,
        PlayerId = result.PlayerId.Value,
        ClientResultId = result.ClientResultId,
        ModeId = result.ModeId,
        LevelId = result.LevelId,
        CharacterId = result.CharacterId,
        ClientVersion = result.ClientVersion,
        Platform = result.Platform,
        MetricsJson = SerializeMetrics(result.Metrics),
        ModifiersJson = SerializeModifiers(result.Modifiers),
        CreatedAt = result.CreatedAt
    };

    private static MatchResult ToDomain(MatchResultRow row) => MatchResult.Rehydrate(
        new MatchResultId(row.Id), new PlayerId(row.PlayerId), row.ClientResultId, row.ModeId, row.LevelId, row.CharacterId,
        row.ClientVersion, row.Platform, DeserializeMetrics(row.Id, row.MetricsJson), DeserializeModifiers(row.Id, row.ModifiersJson), row.CreatedAt);

    private static string SerializeMetrics(MatchResultMetrics metrics) =>
        JsonSerializer.Serialize(metrics.Values.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value));

    private static MatchResultMetrics DeserializeMetrics(Guid resultId, string json)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, double>>(json);
        if (raw is null || raw.Count == 0)
        {
            throw new CorruptMatchResultStateException($"Match result {resultId} has an empty or unparseable metrics document.");
        }

        var metrics = new Dictionary<MatchResultMetric, double>(raw.Count);
        foreach (var (key, value) in raw)
        {
            if (!Enum.TryParse<MatchResultMetric>(key, out var metric))
            {
                throw new CorruptMatchResultStateException(
                    $"Match result {resultId} has a persisted metric name '{key}' that is not a recognized MatchResultMetric.");
            }

            metrics[metric] = value;
        }

        return MatchResultMetrics.Of(metrics);
    }

    private static string SerializeModifiers(MatchResultModifiers modifiers) => JsonSerializer.Serialize(new ModifiersJson
    {
        Hardcore = modifiers.Hardcore,
        TrafficEnabled = modifiers.TrafficEnabled,
        EnemiesEnabled = modifiers.EnemiesEnabled,
        SniperEnabled = modifiers.SniperEnabled
    });

    private static MatchResultModifiers DeserializeModifiers(Guid resultId, string json)
    {
        var parsed = JsonSerializer.Deserialize<ModifiersJson>(json)
            ?? throw new CorruptMatchResultStateException($"Match result {resultId} has an empty or unparseable modifiers document.");

        return MatchResultModifiers.Of(parsed.Hardcore, parsed.TrafficEnabled, parsed.EnemiesEnabled, parsed.SniperEnabled);
    }

    private sealed class ModifiersJson
    {
        public bool Hardcore { get; set; }
        public bool TrafficEnabled { get; set; }
        public bool EnemiesEnabled { get; set; }
        public bool SniperEnabled { get; set; }
    }
}
