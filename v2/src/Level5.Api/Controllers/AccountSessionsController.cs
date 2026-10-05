using Level5.Api.Security;
using Level5.Application.Identity;
using Level5.Domain.Ids;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Level5.Api.Controllers;

public sealed record AccountSessionResponseDto(
    Guid SessionId,
    string ClientKind,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastRefreshedAt,
    DateTimeOffset ExpiresAt,
    bool IsCurrent);

[ApiController]
[Authorize]
[Route("api/v2/me/sessions")]
public sealed class AccountSessionsController(
    ListAccountSessionsUseCase listSessions,
    RevokeAccountSessionUseCase revokeSession,
    RevokeOtherAccountSessionsUseCase revokeOtherSessions,
    RevokeAllAccountSessionsUseCase revokeAllSessions,
    ICurrentAccountAccessor currentAccount,
    ICurrentAuthSessionAccessor currentSession) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AccountSessionResponseDto>>> List(
        CancellationToken cancellationToken)
    {
        var sessions = await listSessions.ExecuteAsync(
            currentAccount.GetCurrentAccountId(), RequireCurrentSession(), cancellationToken);

        return Ok(sessions.Select(session => new AccountSessionResponseDto(
            session.SessionId.Value,
            session.ClientKind.ToString(),
            session.CreatedAt,
            session.LastRefreshedAt,
            session.ExpiresAt,
            session.IsCurrent)));
    }

    [HttpDelete("{sessionId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Revoke(Guid sessionId, CancellationToken cancellationToken)
    {
        await revokeSession.ExecuteAsync(
            currentAccount.GetCurrentAccountId(),
            RequireCurrentSession(),
            new AuthSessionId(sessionId),
            cancellationToken);
        return NoContent();
    }

    [HttpPost("revoke-others")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RevokeOthers(CancellationToken cancellationToken)
    {
        await revokeOtherSessions.ExecuteAsync(
            currentAccount.GetCurrentAccountId(), RequireCurrentSession(), cancellationToken);
        return NoContent();
    }

    [HttpPost("revoke-all")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RevokeAll(CancellationToken cancellationToken)
    {
        await revokeAllSessions.ExecuteAsync(
            currentAccount.GetCurrentAccountId(), RequireCurrentSession(), cancellationToken);
        return NoContent();
    }

    private AuthSessionId RequireCurrentSession()
        => currentSession.GetCurrentAuthSessionId() ?? throw new ReauthenticationRequiredException();
}
