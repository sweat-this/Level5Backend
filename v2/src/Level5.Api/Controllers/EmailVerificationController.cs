using Level5.Api.Security;
using Level5.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Level5.Api.Controllers;

public sealed record CompleteEmailVerificationRequestDto(string Token);

[ApiController]
[Route("api/v2/email-verification")]
public sealed class EmailVerificationController(CompleteEmailVerificationUseCase completeEmailVerification) : ControllerBase
{
    [HttpPost("complete")]
    [AllowAnonymous]
    [EnableRateLimiting(EmailVerificationRateLimitPolicyNames.Complete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Complete(
        CompleteEmailVerificationRequestDto request,
        CancellationToken cancellationToken)
    {
        await completeEmailVerification.ExecuteAsync(request.Token, cancellationToken);
        return NoContent();
    }
}
