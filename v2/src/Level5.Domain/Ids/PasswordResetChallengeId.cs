namespace Level5.Domain.Ids;

public readonly record struct PasswordResetChallengeId(Guid Value)
{
    public static PasswordResetChallengeId New() => new(Guid.NewGuid());
}
