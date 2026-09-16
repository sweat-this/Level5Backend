using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Xunit;

namespace Level5.Domain.Tests.Identity;

public class AuthSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private readonly AccountId _accountId = AccountId.New();

    [Fact]
    public void Create_starts_unrevoked_with_revision_zero_and_an_expiry_after_now()
    {
        var session = AuthSession.Create(_accountId, "hash-1", Now, Lifetime);

        Assert.Equal(0, session.Revision);
        Assert.Null(session.RevokedAt);
        Assert.Equal(Now + Lifetime, session.ExpiresAt);
        Assert.True(session.CanRefresh(Now));
    }

    [Fact]
    public void CanRefresh_is_false_once_expired()
    {
        var session = AuthSession.Create(_accountId, "hash-1", Now, Lifetime);

        Assert.False(session.CanRefresh(Now + Lifetime));
        Assert.False(session.CanRefresh(Now + Lifetime + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void CanRefresh_is_false_once_revoked()
    {
        var session = AuthSession.Create(_accountId, "hash-1", Now, Lifetime);

        session.Revoke(Now);

        Assert.False(session.CanRefresh(Now));
    }

    [Fact]
    public void Rotate_replaces_the_hash_extends_expiry_and_bumps_revision()
    {
        var session = AuthSession.Create(_accountId, "hash-1", Now, Lifetime);
        var rotateAt = Now + TimeSpan.FromDays(1);

        session.Rotate("hash-2", rotateAt, Lifetime);

        Assert.Equal("hash-2", session.RefreshTokenHash);
        Assert.Equal(rotateAt + Lifetime, session.ExpiresAt);
        Assert.Equal(1, session.Revision);
    }

    [Fact]
    public void Rotate_on_an_expired_session_throws()
    {
        var session = AuthSession.Create(_accountId, "hash-1", Now, Lifetime);

        Assert.Throws<SessionNotRefreshableException>(() =>
            session.Rotate("hash-2", Now + Lifetime, Lifetime));
    }

    [Fact]
    public void Rotate_on_a_revoked_session_throws()
    {
        var session = AuthSession.Create(_accountId, "hash-1", Now, Lifetime);
        session.Revoke(Now);

        Assert.Throws<SessionNotRefreshableException>(() =>
            session.Rotate("hash-2", Now, Lifetime));
    }

    [Fact]
    public void Revoke_sets_RevokedAt_and_bumps_revision()
    {
        var session = AuthSession.Create(_accountId, "hash-1", Now, Lifetime);

        session.Revoke(Now);

        Assert.Equal(Now, session.RevokedAt);
        Assert.Equal(1, session.Revision);
    }

    [Fact]
    public void Revoke_twice_is_idempotent_and_does_not_bump_revision_again()
    {
        var session = AuthSession.Create(_accountId, "hash-1", Now, Lifetime);

        session.Revoke(Now);
        session.Revoke(Now + TimeSpan.FromMinutes(1));

        Assert.Equal(Now, session.RevokedAt);
        Assert.Equal(1, session.Revision);
    }

    [Fact]
    public void Rehydrate_preserves_every_field()
    {
        var id = AuthSessionId.New();
        var revokedAt = Now + TimeSpan.FromHours(1);

        var session = AuthSession.Rehydrate(id, _accountId, "hash-1", Now, Now + Lifetime, revokedAt, revision: 3);

        Assert.Equal(id, session.Id);
        Assert.Equal(_accountId, session.AccountId);
        Assert.Equal("hash-1", session.RefreshTokenHash);
        Assert.Equal(Now, session.CreatedAt);
        Assert.Equal(Now + Lifetime, session.ExpiresAt);
        Assert.Equal(revokedAt, session.RevokedAt);
        Assert.Equal(3, session.Revision);
    }
}
