using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Ids;

namespace Level5.Application.Identity;

public sealed record MyEmailStatus(string? Email, bool IsVerified, DateTimeOffset? VerifiedAt);

public sealed class GetMyEmailStatusUseCase(IAccountStore accountStore)
{
    public async Task<MyEmailStatus> ExecuteAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        var account = await accountStore.FindByIdAsync(accountId, cancellationToken)
            ?? throw new NotFoundException("Account not found.");

        return new MyEmailStatus(
            account.Email?.Value,
            account.EmailVerifiedAt is not null,
            account.EmailVerifiedAt);
    }
}
