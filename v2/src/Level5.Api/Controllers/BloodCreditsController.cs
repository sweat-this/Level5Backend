using Level5.Api.Security;
using Level5.Application.BloodMoney;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Level5.Api.Controllers;

public sealed record BloodCreditBalanceResponseDto(long AvailableCredits);

[ApiController]
[Authorize]
[Route("api/v2/games/blood-money/me/credits")]
public sealed class BloodCreditsController(GetMyBloodCreditBalanceUseCase balance, ICurrentPlayerProvider currentPlayer) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<BloodCreditBalanceResponseDto>> Get(CancellationToken cancellationToken)
    {
        var playerId = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        return Ok(new BloodCreditBalanceResponseDto(await balance.ExecuteAsync(playerId, cancellationToken)));
    }
}
