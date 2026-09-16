using Level5.Api.Security;
using Level5.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Level5.Api.Controllers;

public sealed record CurrentAccountResponseDto(Guid AccountId, string Username, string Status, Guid PlayerId, DateTimeOffset CreatedAt);

/// <summary>
/// The private authenticated-account identity - deliberately separate from
/// <c>PlayersController.GetMyPlayerId</c> (<c>/api/v2/players/me</c>), which is the public
/// in-game identity. This is the account behind it: username, status, and when it was created.
/// </summary>
[ApiController]
[Route("api/v2")]
[Authorize]
public sealed class AccountController(GetCurrentAccountUseCase getCurrentAccount, ICurrentAccountAccessor currentAccount) : ControllerBase
{
    [HttpGet("me")]
    public async Task<ActionResult<CurrentAccountResponseDto>> GetMe(CancellationToken cancellationToken)
    {
        var accountId = currentAccount.GetCurrentAccountId();
        var view = await getCurrentAccount.ExecuteAsync(accountId, cancellationToken);

        return Ok(new CurrentAccountResponseDto(
            view.AccountId.Value, view.Username, view.Status.ToString(), view.PlayerId.Value, view.CreatedAt));
    }
}
