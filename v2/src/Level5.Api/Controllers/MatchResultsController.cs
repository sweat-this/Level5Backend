using Level5.Api.Security;
using Level5.Application.Common;
using Level5.Application.Results;
using Level5.Domain.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Level5.Api.Controllers;

/// <summary>
/// The named metric payload for a match-result submission. Metric keys must be one of the
/// stable <see cref="MatchResultMetric"/> names, never a positional index - an unrecognized key
/// is a 400, never silently ignored.
/// </summary>
public sealed record MatchResultModifiersDto(
    bool Hardcore = false, bool TrafficEnabled = false, bool EnemiesEnabled = false, bool SniperEnabled = false);

/// <summary>
/// The client-reported gameplay/context data for one ordinary completed match.
/// <see cref="ClientResultId"/> is mandatory and scopes idempotency together with the
/// authenticated acting player - it is never taken from the request. PlayerId, CreatedAt, and any
/// derived ranking/leaderboard data are never accepted from the client.
/// </summary>
public sealed record SubmitMatchResultDto(
    Guid ClientResultId, int ModeId, int LevelId, string CharacterId, string ClientVersion, string Platform,
    IReadOnlyDictionary<string, double> Metrics, MatchResultModifiersDto? Modifiers = null);

public sealed record MatchResultResponseDto(
    Guid Id, Guid PlayerId, Guid ClientResultId, int ModeId, int LevelId, string CharacterId,
    string ClientVersion, string Platform, IReadOnlyDictionary<string, double> Metrics, MatchResultModifiersDto Modifiers,
    DateTimeOffset CreatedAt);

/// <summary>
/// General match-result ingestion: records an ordinary completed match as an immutable,
/// authenticated, retry-safe result for later leaderboard consumption. Deliberately separate from
/// the correspondence <c>SeriesController</c> - this is not a <c>VersusSeries</c>/
/// <c>AttemptResult</c> and never mutates a server-owned competitive aggregate.
/// </summary>
[ApiController]
[Route("api/v2/match-results")]
[Authorize]
public sealed class MatchResultsController(SubmitMatchResultUseCase submitMatchResult, ICurrentPlayerProvider currentPlayer) : ControllerBase
{
    private static readonly IReadOnlyDictionary<string, MatchResultMetric> MetricsByName =
        Enum.GetValues<MatchResultMetric>().ToDictionary(metric => metric.ToString(), StringComparer.OrdinalIgnoreCase);

    [HttpPost]
    public async Task<ActionResult<MatchResultResponseDto>> Submit(SubmitMatchResultDto request, CancellationToken cancellationToken)
    {
        var me = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var metrics = ParseMetrics(request.Metrics);
        var modifiers = ToDomain(request.Modifiers ?? new MatchResultModifiersDto());

        var result = await submitMatchResult.ExecuteAsync(
            new SubmitMatchResultRequest(
                me, request.ClientResultId, request.ModeId, request.LevelId, request.CharacterId,
                request.ClientVersion, request.Platform, metrics, modifiers),
            cancellationToken);

        return Ok(ToDto(result));
    }

    private static MatchResultMetrics ParseMetrics(IReadOnlyDictionary<string, double>? metrics)
    {
        if (metrics is null || metrics.Count == 0)
        {
            throw new ValidationFailedException("At least one result metric is required.");
        }

        var parsed = new Dictionary<MatchResultMetric, double>(metrics.Count);
        foreach (var (key, value) in metrics)
        {
            if (!MetricsByName.TryGetValue(key, out var metric))
            {
                throw new ValidationFailedException($"Unknown result metric '{key}'.");
            }

            if (!parsed.TryAdd(metric, value))
            {
                throw new ValidationFailedException($"Result metric '{metric}' was supplied more than once.");
            }
        }

        return MatchResultMetrics.Of(parsed);
    }

    private static MatchResultModifiers ToDomain(MatchResultModifiersDto dto)
        => MatchResultModifiers.Of(dto.Hardcore, dto.TrafficEnabled, dto.EnemiesEnabled, dto.SniperEnabled);

    private static MatchResultResponseDto ToDto(MatchResult result) => new(
        result.Id.Value, result.PlayerId.Value, result.ClientResultId, result.ModeId, result.LevelId, result.CharacterId,
        result.ClientVersion, result.Platform,
        result.Metrics.Values.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
        new MatchResultModifiersDto(result.Modifiers.Hardcore, result.Modifiers.TrafficEnabled, result.Modifiers.EnemiesEnabled, result.Modifiers.SniperEnabled),
        result.CreatedAt);
}
