using Level5.Api.Security;
using Level5.Application.Platform;
using Level5.Domain.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Level5.Api.Controllers;

public sealed record ProductAccessResponseDto(
    string ProductId,
    bool HasAccess,
    string? Kind,
    DateTimeOffset? ExpiresAt);

public sealed record ActiveEntitlementResponseDto(
    string ProductId,
    string Kind,
    DateTimeOffset? ExpiresAt);

[ApiController]
[Route("api/v2/platform/me/entitlements")]
[Authorize]
public sealed class PlatformEntitlementsController(
    CheckMyProductAccessUseCase checkAccess,
    ListMyEntitlementsUseCase listEntitlements,
    ICurrentPlayerProvider currentPlayer) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ActiveEntitlementResponseDto>>> List(
        CancellationToken cancellationToken)
    {
        var playerId = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var entitlements = await listEntitlements.ExecuteAsync(
            new ListMyEntitlementsRequest(playerId),
            cancellationToken);

        return Ok(entitlements.Select(entitlement => new ActiveEntitlementResponseDto(
            entitlement.ProductId.Value,
            entitlement.Kind.ToString(),
            entitlement.ExpiresAt)));
    }

    [HttpGet("{productId}")]
    public async Task<ActionResult<ProductAccessResponseDto>> Check(
        string productId,
        CancellationToken cancellationToken)
    {
        var playerId = await currentPlayer.GetCurrentPlayerIdAsync(cancellationToken);
        var result = await checkAccess.ExecuteAsync(
            new CheckMyProductAccessRequest(playerId, ProductId.Create(productId)),
            cancellationToken);

        return Ok(new ProductAccessResponseDto(
            result.ProductId.Value,
            result.HasAccess,
            result.Kind?.ToString(),
            result.ExpiresAt));
    }
}
