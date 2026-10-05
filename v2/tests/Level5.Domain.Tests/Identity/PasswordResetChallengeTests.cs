using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Xunit;

namespace Level5.Domain.Tests.Identity;

public sealed class PasswordResetChallengeTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    [Fact]
    public void Rotation_replaces_token_and_consumption_is_single_use()
    {
        var challenge = Create();
        challenge.Consume(Now.AddMinutes(1));
        challenge.Rotate(Email.Create("new@example.com"), "hash-2", Now.AddMinutes(2), Lifetime);

        Assert.Null(challenge.ConsumedAt);
        Assert.Equal("hash-2", challenge.TokenHash);
        Assert.Equal(2, challenge.Revision);

        challenge.Consume(Now.AddMinutes(3));
        Assert.Throws<PasswordResetChallengeInvalidException>(() => challenge.Consume(Now.AddMinutes(4)));
    }

    [Fact]
    public void Expired_challenge_is_not_usable()
    {
        var challenge = Create();
        Assert.False(challenge.CanComplete(Now + Lifetime));
        Assert.Throws<PasswordResetChallengeInvalidException>(() => challenge.Consume(Now + Lifetime));
    }

    private static PasswordResetChallenge Create() => PasswordResetChallenge.Create(
        AccountId.New(), Email.Create("target@example.com"), "hash-1", Now, Lifetime);
}
