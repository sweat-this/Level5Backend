using Level5.Application.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Level5.Api.Controllers;

public sealed record RegisterRequestDto(string Username, string Password, string DisplayName);

public sealed record LoginRequestDto(string Username, string Password);

public sealed record AccessTokenResponseDto(string AccessToken, DateTimeOffset ExpiresAt, Guid PlayerId);

[ApiController]
[Route("api/v2/auth")]
[EnableRateLimiting("AuthPolicy")]
// Register/Login are the two halves of one resource (account authentication), sharing this
// route prefix and rate-limit policy - splitting them would fragment that, not simplify it.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S6960", Justification = "Register/Login are one cohesive auth resource, consistent with the thin-controller/one-use-case-per-action pattern used throughout this API.")]
public sealed class AuthController(RegisterAccountUseCase registerAccount, LoginUseCase login) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<AccessTokenResponseDto>> Register(RegisterRequestDto request, CancellationToken cancellationToken)
    {
        var result = await registerAccount.ExecuteAsync(
            new RegisterAccountRequest(request.Username, request.Password, request.DisplayName), cancellationToken);

        return Ok(new AccessTokenResponseDto(result.AccessToken.Value, result.AccessToken.ExpiresAt, result.PlayerId.Value));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AccessTokenResponseDto>> Login(LoginRequestDto request, CancellationToken cancellationToken)
    {
        var result = await login.ExecuteAsync(new LoginRequest(request.Username, request.Password), cancellationToken);
        return Ok(new AccessTokenResponseDto(result.AccessToken.Value, result.AccessToken.ExpiresAt, result.PlayerId.Value));
    }
}
