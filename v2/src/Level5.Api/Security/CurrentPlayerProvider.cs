using Level5.Application.Abstractions;
using Level5.Domain.Ids;

namespace Level5.Api.Security;

public sealed class CurrentPlayerProvider(ICurrentAccountAccessor currentAccount, IPlayerProfileStore playerProfileStore) : ICurrentPlayerProvider
{
    public async Task<PlayerId> GetCurrentPlayerIdAsync(CancellationToken cancellationToken)
    {
        var accountId = currentAccount.GetCurrentAccountId();

        var profile = await playerProfileStore.FindByAccountIdAsync(accountId, cancellationToken)
            ?? throw new InvalidOperationException($"Account {accountId} has no player profile.");

        return profile.Id;
    }
}
