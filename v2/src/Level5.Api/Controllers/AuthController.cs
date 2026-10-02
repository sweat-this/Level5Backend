using Level5.Application.Identity;
using Level5.Api.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Level5.Api.Controllers;

public sealed record RegisterRequestDto(string Username, string Password, string DisplayName);

public sealed record LoginRequestDto(string Username, string Password);

public sealed record RefreshRequestDto(string RefreshToken);

public sealed record LogoutRequestDto(string RefreshToken);

public sealed record AccessTokenResponseDto(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    Guid PlayerId,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

[ApiController]
[Route("api/v2/auth")]
// Register/Login/Refresh/Logout remain one cohesive auth resource. Rate limiting is action-scoped
// because each operation has an independent per-IP fixed-window budget.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S6960", Justification = "Register/Login/Refresh/Logout are one cohesive auth resource, consistent with the thin-controller/one-use-case-per-action pattern used throughout this API.")]
public sealed class AuthController(
    RegisterAccountUseCase registerAccount,
    LoginUseCase login,
    RefreshSessionUseCase refreshSession,
    LogoutUseCase logout) : ControllerBase
{
    [HttpPost("register")]
    [EnableRateLimiting(AuthRateLimitPolicyNames.Register)]
    public async Task<ActionResult<AccessTokenResponseDto>> Register(RegisterRequestDto request, CancellationToken cancellationToken)
    {
        var result = await registerAccount.ExecuteAsync(
            new RegisterAccountRequest(request.Username, request.Password, request.DisplayName), cancellationToken);

        return Ok(new AccessTokenResponseDto(
            result.AccessToken.Value, result.AccessToken.ExpiresAt, result.PlayerId.Value,
            result.RefreshToken, result.RefreshTokenExpiresAt));
    }

    [HttpPost("login")]
    [EnableRateLimiting(AuthRateLimitPolicyNames.Login)]
    public async Task<ActionResult<AccessTokenResponseDto>> Login(LoginRequestDto request, CancellationToken cancellationToken)
    {
        var result = await login.ExecuteAsync(new LoginRequest(request.Username, request.Password), cancellationToken);

        return Ok(new AccessTokenResponseDto(
            result.AccessToken.Value, result.AccessToken.ExpiresAt, result.PlayerId.Value,
            result.RefreshToken, result.RefreshTokenExpiresAt));
    }

    [HttpPost("refresh")]
    [EnableRateLimiting(AuthRateLimitPolicyNames.Refresh)]
    public async Task<ActionResult<AccessTokenResponseDto>> Refresh(RefreshRequestDto request, CancellationToken cancellationToken)
    {
        var result = await refreshSession.ExecuteAsync(new RefreshSessionRequest(request.RefreshToken), cancellationToken);

        return Ok(new AccessTokenResponseDto(
            result.AccessToken.Value, result.AccessToken.ExpiresAt, result.PlayerId.Value,
            result.RefreshToken, result.RefreshTokenExpiresAt));
    }

    // No [Authorize] here on purpose: a logout call must work even when the caller's access token
    // has already expired, since the whole point is to invalidate the longer-lived refresh
    // credential it presents in the body - see LogoutUseCase and the V2 README.
    [HttpPost("logout")]
    [EnableRateLimiting(AuthRateLimitPolicyNames.Logout)]
    public async Task<IActionResult> Logout(LogoutRequestDto request, CancellationToken cancellationToken)
    {
        await logout.ExecuteAsync(new LogoutRequest(request.RefreshToken), cancellationToken);
        return NoContent();
    }
}
