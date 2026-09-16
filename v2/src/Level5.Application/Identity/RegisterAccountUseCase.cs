using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Domain.Players;

namespace Level5.Application.Identity;

public sealed record RegisterAccountRequest(string Username, string Password, string DisplayName);

public sealed record RegisterAccountResult(AccountId AccountId, PlayerId PlayerId, string PlayerTag, AccessToken AccessToken);

/// <summary>Creates a new account and its initial public player profile in one atomic operation.</summary>
public sealed class RegisterAccountUseCase(
    IAccountStore accountStore,
    IPlayerProfileStore playerProfileStore,
    IPasswordHasher passwordHasher,
    ITokenIssuer tokenIssuer,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    private const int MaxTagAssignmentAttempts = 10;

    public async Task<RegisterAccountResult> ExecuteAsync(RegisterAccountRequest request, CancellationToken cancellationToken)
    {
        var username = Username.Create(request.Username);

        if (await accountStore.UsernameExistsAsync(username, cancellationToken))
        {
            throw new ConflictException("Username is already taken.");
        }

        var now = clock.UtcNow;
        var account = Account.Register(username, passwordHasher.Hash(request.Password), now);
        await accountStore.AddAsync(account, cancellationToken);

        var tag = await AssignAvailableTagAsync(request.DisplayName, cancellationToken);
        var profile = PlayerProfile.Create(account.Id, request.DisplayName, tag, now);
        await playerProfileStore.AddAsync(profile, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        var accessToken = tokenIssuer.IssueAccessToken(account.Id);
        return new RegisterAccountResult(account.Id, profile.Id, tag.Value, accessToken);
    }

    private async Task<PlayerTag> AssignAvailableTagAsync(string displayName, CancellationToken cancellationToken)
    {
        var baseHandle = new string([.. displayName.Where(char.IsLetterOrDigit)]);
        if (baseHandle.Length < 2)
        {
            baseHandle = "PLAYER";
        }
        else if (baseHandle.Length > 20)
        {
            baseHandle = baseHandle[..20];
        }

        for (var attempt = 0; attempt < MaxTagAssignmentAttempts; attempt++)
        {
            var discriminator = Random.Shared.Next(1, 9999).ToString("D4");
            var candidate = PlayerTag.Create($"{baseHandle}#{discriminator}");

            if (!await playerProfileStore.TagExistsAsync(candidate, cancellationToken))
            {
                return candidate;
            }
        }

        throw new ConflictException("Could not assign a unique player tag. Please try again.");
    }
}
