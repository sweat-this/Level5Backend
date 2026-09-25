using Level5.Application.Abstractions;
using Level5.Domain.Migration;

namespace Level5.Application.Migration;

public sealed record LegacyLinkConsistencyResult(bool IsConsistent, string? Reason);

/// <summary>
/// The single definition of "is this legacy_account_links row consistent" - shared by
/// <see cref="ImportLegacyAccountUseCase"/>'s idempotent-resume check and the standalone
/// <c>verify</c> command, so the two can never define consistency differently.
/// </summary>
public static class LegacyLinkConsistencyChecker
{
    public static async Task<LegacyLinkConsistencyResult> CheckAsync(
        LegacyAccountLink link,
        IAccountStore accountStore,
        IPlayerProfileStore playerProfileStore,
        CancellationToken cancellationToken)
    {
        var account = await accountStore.FindByIdAsync(link.AccountId, cancellationToken);
        if (account is null)
        {
            return new LegacyLinkConsistencyResult(false,
                $"legacy_account_links row for legacy user {link.LegacyUserId} points to Account {link.AccountId}, but that account does not exist.");
        }

        var profile = await playerProfileStore.FindByIdAsync(link.PlayerId, cancellationToken);
        if (profile is null)
        {
            return new LegacyLinkConsistencyResult(false,
                $"legacy_account_links row for legacy user {link.LegacyUserId} points to PlayerProfile {link.PlayerId}, but that profile does not exist.");
        }

        if (profile.AccountId != link.AccountId)
        {
            return new LegacyLinkConsistencyResult(false,
                $"legacy_account_links row for legacy user {link.LegacyUserId} points to PlayerProfile {link.PlayerId}, " +
                $"but that profile belongs to Account {profile.AccountId}, not the linked Account {link.AccountId}.");
        }

        return new LegacyLinkConsistencyResult(true, null);
    }
}
