using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Xunit;

namespace Level5.Domain.Tests.Identity;

public sealed class EmailVerificationChallengeTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    [Fact]
    public void Create_starts_unconsumed_with_revision_zero_and_expiry()
    {
        var challenge = Create();

        Assert.Null(challenge.ConsumedAt);
        Assert.Equal(0, challenge.Revision);
        Assert.Equal(Now + Lifetime, challenge.ExpiresAt);
        Assert.True(challenge.CanComplete(Now));
    }

    [Fact]
    public void Rotate_replaces_target_and_hash_and_invalidates_consumption_state()
    {
        var challenge = Create();
        challenge.Consume(Now.AddMinutes(1));

        challenge.Rotate(Email.Create("new@example.com"), "hash-2", Now.AddMinutes(2), Lifetime);

        Assert.Equal("new@example.com", challenge.TargetEmail.Value);
        Assert.Equal("hash-2", challenge.TokenHash);
        Assert.Null(challenge.ConsumedAt);
        Assert.Equal(2, challenge.Revision);
    }

    [Fact]
    public void Consume_is_single_use_and_increments_revision()
    {
        var challenge = Create();

        challenge.Consume(Now.AddMinutes(1));

        Assert.Equal(Now.AddMinutes(1), challenge.ConsumedAt);
        Assert.Equal(1, challenge.Revision);
        Assert.Throws<EmailVerificationChallengeInvalidException>(() =>
            challenge.Consume(Now.AddMinutes(2)));
    }

    [Fact]
    public void Expired_challenge_cannot_be_consumed()
    {
        var challenge = Create();

        Assert.False(challenge.CanComplete(Now + Lifetime));
        Assert.Throws<EmailVerificationChallengeInvalidException>(() =>
            challenge.Consume(Now + Lifetime));
    }

    private static EmailVerificationChallenge Create() => EmailVerificationChallenge.Create(
        AccountId.New(), Email.Create("target@example.com"), "hash-1", Now, Lifetime);
}
