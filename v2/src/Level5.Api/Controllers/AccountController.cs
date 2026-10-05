using Level5.Api.Security;
using Level5.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Level5.Api.Controllers;

public sealed record CurrentAccountResponseDto(Guid AccountId, string Username, string Status, Guid PlayerId, DateTimeOffset CreatedAt);
public sealed record MyEmailStatusResponseDto(string? Email, bool IsVerified, DateTimeOffset? VerifiedAt);
public sealed record RequestEmailVerificationDto(string CurrentPassword, string Email);
public sealed record EmailVerificationDispatchResponseDto(bool AlreadyVerified, DateTimeOffset? ExpiresAt);

/// <summary>
/// The private authenticated-account identity - deliberately separate from
/// <c>PlayersController.GetMyPlayerId</c> (<c>/api/v2/players/me</c>), which is the public
/// in-game identity. This is the account behind it: username, status, and when it was created.
/// </summary>
[ApiController]
[Route("api/v2")]
[Authorize]
public sealed class AccountController(
    GetCurrentAccountUseCase getCurrentAccount,
    GetMyEmailStatusUseCase getMyEmailStatus,
    RequestEmailVerificationUseCase requestEmailVerification,
    ResendEmailVerificationUseCase resendEmailVerification,
    ICurrentAccountAccessor currentAccount) : ControllerBase
{
    [HttpGet("me")]
    public async Task<ActionResult<CurrentAccountResponseDto>> GetMe(CancellationToken cancellationToken)
    {
        var accountId = currentAccount.GetCurrentAccountId();
        var view = await getCurrentAccount.ExecuteAsync(accountId, cancellationToken);

        return Ok(new CurrentAccountResponseDto(
            view.AccountId.Value, view.Username, view.Status.ToString(), view.PlayerId.Value, view.CreatedAt));
    }

    [HttpGet("me/email")]
    public async Task<ActionResult<MyEmailStatusResponseDto>> GetMyEmail(CancellationToken cancellationToken)
    {
        var view = await getMyEmailStatus.ExecuteAsync(currentAccount.GetCurrentAccountId(), cancellationToken);
        return Ok(new MyEmailStatusResponseDto(view.Email, view.IsVerified, view.VerifiedAt));
    }

    [HttpPost("me/email/verification")]
    [EnableRateLimiting(EmailVerificationRateLimitPolicyNames.Request)]
    [ProducesResponseType(typeof(EmailVerificationDispatchResponseDto), StatusCodes.Status202Accepted)]
    public async Task<ActionResult<EmailVerificationDispatchResponseDto>> RequestEmailVerification(
        RequestEmailVerificationDto request,
        CancellationToken cancellationToken)
    {
        var result = await requestEmailVerification.ExecuteAsync(
            new(currentAccount.GetCurrentAccountId(), request.CurrentPassword, request.Email), cancellationToken);
        return Accepted(new EmailVerificationDispatchResponseDto(result.AlreadyVerified, result.ExpiresAt));
    }

    [HttpPost("me/email/verification/resend")]
    [EnableRateLimiting(EmailVerificationRateLimitPolicyNames.Resend)]
    [ProducesResponseType(typeof(EmailVerificationDispatchResponseDto), StatusCodes.Status202Accepted)]
    public async Task<ActionResult<EmailVerificationDispatchResponseDto>> ResendEmailVerification(
        CancellationToken cancellationToken)
    {
        var result = await resendEmailVerification.ExecuteAsync(
            currentAccount.GetCurrentAccountId(), cancellationToken);
        return Accepted(new EmailVerificationDispatchResponseDto(AlreadyVerified: false, result.ExpiresAt));
    }
}
