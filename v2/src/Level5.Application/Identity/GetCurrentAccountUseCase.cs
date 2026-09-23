using Level5.Application.Abstractions;
using Level5.Domain.Identity;
using Level5.Domain.Ids;

namespace Level5.Application.Identity;

public sealed record CurrentAccountView(AccountId AccountId, string Username, AccountStatus Status, PlayerId PlayerId, DateTimeOffset CreatedAt);

/// <summary>
/// The private authenticated-account view behind <c>GET /api/v2/me</c> - deliberately separate
/// from the public in-game <c>PlayerProfile</c> exposed by <c>/api/v2/players/me</c>. Takes an
/// already-resolved <see cref="AccountId"/> rather than any HTTP/claims type, so this stays
/// testable without ASP.NET Core.
/// </summary>
public sealed class GetCurrentAccountUseCase(IAccountStore accountStore, IPlayerProfileStore playerProfileStore)
{
    public async Task<CurrentAccountView> ExecuteAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        var account = await accountStore.FindByIdAsync(accountId, cancellationToken)
            ?? throw new InvalidOperationException($"Authenticated account {accountId} was not found.");

        var profile = await playerProfileStore.FindByAccountIdAsync(accountId, cancellationToken)
            ?? throw new InvalidOperationException($"Account {accountId} has no player profile.");

        return new CurrentAccountView(account.Id, account.Username.Value, account.Status, profile.Id, account.CreatedAt);
    }
}
