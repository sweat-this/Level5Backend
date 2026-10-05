namespace Level5.Domain.Ids;

public readonly record struct EmailVerificationChallengeId(Guid Value)
{
    public static EmailVerificationChallengeId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
