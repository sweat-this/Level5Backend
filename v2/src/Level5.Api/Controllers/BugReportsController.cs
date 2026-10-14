using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Level5.Api.Security;
using Level5.Application.Platform;
using Level5.Domain.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Level5.Api.Controllers;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitBugReportDto
{
    [Required] public required Guid ClientSubmissionId { get; init; }
    [Required, MaxLength(32)] public required string GameKey { get; init; }
    [Required, MaxLength(BugReportLimits.Title)] public required string Title { get; init; }
    [Required, MaxLength(BugReportLimits.Description)] public required string Description { get; init; }
    [MaxLength(32)] public string? Category { get; init; }
    [MaxLength(BugReportLimits.StepsToReproduce)] public string? StepsToReproduce { get; init; }
    [MaxLength(BugReportLimits.GameVersion)] public string? GameVersion { get; init; }
}

public sealed record BugReportReceiptDto(Guid Id);

[ApiController, Authorize]
[Route("api/v2/platform/bug-reports")]
public sealed class BugReportsController(SubmitBugReportUseCase submit, ICurrentPlayerProvider currentPlayer) : ControllerBase
{
    [HttpPost, EnableRateLimiting("platform-bug-reports")]
    [RequestSizeLimit(131072)]
    [ProducesResponseType<BugReportReceiptDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<BugReportReceiptDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<BugReportReceiptDto>> Submit(SubmitBugReportDto request, CancellationToken cancellationToken)
    {
        var reporter = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var result = await submit.ExecuteAsync(new(reporter, request.ClientSubmissionId, request.GameKey,
            request.Title, request.Description, request.Category, request.StepsToReproduce, request.GameVersion), cancellationToken);
        // No public retrieval URI exists for private reports.
        return StatusCode(result.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK, new BugReportReceiptDto(result.Id));
    }
}
