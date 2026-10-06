using Level5.Api.Security;
using Level5.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Level5.Api.Controllers;

public sealed record CompleteEmailChangeRequestDto(string Token);

[ApiController]
[Route("api/v2/email-change")]
public sealed class EmailChangeController(CompleteEmailChangeUseCase completeEmailChange) : ControllerBase
{
    [HttpPost("complete")]
    [AllowAnonymous]
    [EnableRateLimiting(EmailChangeRateLimitPolicyNames.Complete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Complete(
        CompleteEmailChangeRequestDto request,
        CancellationToken cancellationToken)
    {
        await completeEmailChange.ExecuteAsync(request.Token, cancellationToken);
        return NoContent();
    }
}
