using Level5.Domain.Results;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.Persistence;

public sealed class Level5V2DbContext(DbContextOptions<Level5V2DbContext> options) : DbContext(options)
{
    public DbSet<AccountRow> Accounts => Set<AccountRow>();
    public DbSet<AuthSessionRow> AuthSessions => Set<AuthSessionRow>();
    public DbSet<PlayerProfileRow> PlayerProfiles => Set<PlayerProfileRow>();
    public DbSet<FriendRequestRow> FriendRequests => Set<FriendRequestRow>();
    public DbSet<FriendshipRow> Friendships => Set<FriendshipRow>();
    public DbSet<VersusSeriesRow> VersusSeries => Set<VersusSeriesRow>();
    public DbSet<MatchResultRow> MatchResults => Set<MatchResultRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountRow>(entity =>
        {
            entity.ToTable("accounts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Username).HasMaxLength(32);
            entity.Property(e => e.UsernameCanonical).HasMaxLength(32);
            entity.Property(e => e.Email).HasMaxLength(320);
            entity.Property(e => e.EmailCanonical).HasMaxLength(320);
            entity.Property(e => e.Status).HasMaxLength(16);
            entity.Property(e => e.PasswordHash).HasMaxLength(512);
            entity.HasIndex(e => e.UsernameCanonical).IsUnique();
            // Postgres unique indexes treat NULL as distinct from every other value, so this
            // enforces uniqueness only when an email is actually present - unlimited accounts
            // with no email can coexist.
            entity.HasIndex(e => e.EmailCanonical).IsUnique();
        });

        modelBuilder.Entity<AuthSessionRow>(entity =>
        {
            entity.ToTable("auth_sessions");
            entity.HasKey(e => e.Id);
            // SHA-256 hex digest is 64 chars; headroom left in case the hashing scheme changes.
            entity.Property(e => e.RefreshTokenHash).HasMaxLength(128);
            entity.HasIndex(e => e.RefreshTokenHash).IsUnique();
            entity.HasIndex(e => e.AccountId);
            // The optimistic-concurrency token: rotation/revocation writes are conditioned on this
            // column matching the value that was read, via ExecuteUpdate's WHERE clause in
            // AuthSessionStore.TrySaveAsync - the same pattern VersusSeriesRow uses below.
            entity.Property(e => e.Revision).IsConcurrencyToken();
            // Restrict, not cascade: this migration defines the relationship, not
            // account-deletion semantics (there is no account-deletion feature yet) - a stray
            // delete must fail loudly rather than silently orphan sessions.
            entity.HasOne<AccountRow>()
                .WithMany()
                .HasForeignKey(e => e.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlayerProfileRow>(entity =>
        {
            entity.ToTable("player_profiles");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DisplayName).HasMaxLength(32);
            entity.Property(e => e.Tag).HasMaxLength(32);
            entity.HasIndex(e => e.AccountId).IsUnique();
            entity.HasIndex(e => e.Tag).IsUnique();
            // Modeled as one-to-many at the FK level (not WithOne/one-to-one) because neither Row
            // type has a navigation property, and EF's one-to-one navigation fixup severs an
            // already-tracked dependent's FK when a second one referencing the same principal is
            // added to the same context before saving - exactly what the "two profiles, same
            // account" constraint test does. The actual one-profile-per-account invariant is
            // enforced by the separate unique index on AccountId above, independent of how the FK
            // relationship itself is shaped.
            // Restrict (not cascade) - this issue defines the relationship, not account deletion
            // semantics, so a stray delete must fail loudly rather than silently orphan/prune data.
            entity.HasOne<AccountRow>()
                .WithMany()
                .HasForeignKey(e => e.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FriendRequestRow>(entity =>
        {
            entity.ToTable("friend_requests");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasMaxLength(16);
            entity.HasIndex(e => new { e.ToPlayerId, e.Status });
            entity.HasIndex(e => new { e.FromPlayerId, e.Status });
            // Enforced again in the application layer before insert, but a unique partial index
            // is what actually prevents a duplicate pending request under concurrent requests.
            entity.HasIndex(e => new { e.FromPlayerId, e.ToPlayerId })
                .HasFilter("\"Status\" = 'Pending'")
                .IsUnique();
            // Same-direction duplicates alone aren't enough: A->B and B->A are two distinct rows
            // under the index above and could both land as Pending from a genuine race between
            // crossed sends. This second partial index, keyed on the canonical (order-independent)
            // pair, is what actually blocks that - only one Pending request may exist between any
            // two players regardless of direction.
            entity.HasIndex(e => new { e.LowerPlayerId, e.UpperPlayerId })
                .HasFilter("\"Status\" = 'Pending'")
                .IsUnique();
            // The optimistic-concurrency token: Accept/Decline/Cancel are written through the
            // tracked entity in the same SaveChanges call that inserts the resulting Friendship
            // (Accept only), so a stale write's failed concurrency check rolls back both together
            // rather than needing a separate explicit transaction.
            entity.Property(e => e.Revision).IsConcurrencyToken();
            // Restrict (not cascade), same reasoning as accounts/player_profiles above: these
            // relationships define referential integrity, not a player-deletion feature, so a
            // stray delete must fail loudly rather than silently orphan or prune social history.
            // Four separate FKs to the same principal table (issue #20) - FromPlayerId/ToPlayerId
            // carry direction, LowerPlayerId/UpperPlayerId are the derived canonical pair (see the
            // comment on those columns in FriendRequestRow); all four must stay valid player
            // references independently since they're populated from the same two players.
            entity.HasOne<PlayerProfileRow>()
                .WithMany()
                .HasForeignKey(e => e.FromPlayerId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlayerProfileRow>()
                .WithMany()
                .HasForeignKey(e => e.ToPlayerId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlayerProfileRow>()
                .WithMany()
                .HasForeignKey(e => e.LowerPlayerId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlayerProfileRow>()
                .WithMany()
                .HasForeignKey(e => e.UpperPlayerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FriendshipRow>(entity =>
        {
            entity.ToTable("friendships");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.LowerPlayerId, e.UpperPlayerId }).IsUnique();
            // Restrict, not cascade - see the FriendRequestRow FKs above; an accepted friendship
            // is history that must not silently disappear if a player row is ever removed.
            entity.HasOne<PlayerProfileRow>()
                .WithMany()
                .HasForeignKey(e => e.LowerPlayerId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlayerProfileRow>()
                .WithMany()
                .HasForeignKey(e => e.UpperPlayerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VersusSeriesRow>(entity =>
        {
            entity.ToTable("competitive_series");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasMaxLength(24);
            entity.Property(e => e.StateJson).HasColumnType("jsonb");
            entity.HasIndex(e => new { e.OpponentId, e.Status });
            entity.HasIndex(e => new { e.ChallengerId, e.Status });
            // Create idempotency (issue #10): Postgres unique indexes treat NULL as distinct from
            // every other value, so this enforces uniqueness only once a create actually supplies
            // a ClientRequestId - any number of rows with no key (e.g. seeded directly by tests)
            // can coexist for the same challenger.
            entity.HasIndex(e => new { e.ChallengerId, e.ClientRequestId }).IsUnique();
            // The optimistic-concurrency token: writes are conditioned on this column matching
            // the value that was read, via ExecuteUpdate's WHERE clause in VersusSeriesStore.
            entity.Property(e => e.Revision).IsConcurrencyToken();
            // Restrict, not cascade - see the FriendRequestRow FKs above; correspondence history
            // must not silently disappear if a player row is ever removed. WinnerId stays
            // nullable (a series with no winner yet, or a draw) but must reference a real player
            // whenever it is set - EF/Npgsql make an optional FK nullable automatically.
            entity.HasOne<PlayerProfileRow>()
                .WithMany()
                .HasForeignKey(e => e.ChallengerId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlayerProfileRow>()
                .WithMany()
                .HasForeignKey(e => e.OpponentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlayerProfileRow>()
                .WithMany()
                .HasForeignKey(e => e.WinnerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MatchResultRow>(entity =>
        {
            entity.ToTable("match_results");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CharacterId).HasMaxLength(MatchResultFieldLimits.CharacterIdMaxLength);
            entity.Property(e => e.ClientVersion).HasMaxLength(MatchResultFieldLimits.ClientVersionMaxLength);
            entity.Property(e => e.Platform).HasMaxLength(MatchResultFieldLimits.PlatformMaxLength);
            entity.Property(e => e.MetricsJson).HasColumnType("jsonb");
            entity.Property(e => e.ModifiersJson).HasColumnType("jsonb");
            // Submission idempotency: a duplicate (PlayerId, ClientResultId) pair fails this
            // unique index and is translated to ConflictException - SubmitMatchResultUseCase
            // reloads and resolves it as a replay/conflict exactly like a sequential retry.
            entity.HasIndex(e => new { e.PlayerId, e.ClientResultId }).IsUnique();
            // Measured, not speculative (issue: leaderboard reads, section 16): EXPLAIN ANALYZE
            // against ~60k rows spread evenly across 21 modes showed a plain B-tree index on ModeId
            // cuts the leaderboard query from a full sequential scan (~5.8ms) to a bitmap index scan
            // (~3.2ms) - see LeaderboardQueryPlanTests. No JSON-expression index on the ranking
            // metric was added; ModeId alone was enough to justify itself at this scale.
            entity.HasIndex(e => e.ModeId);
            // Restrict, not cascade - match results are immutable history, same reasoning as
            // competitive_series/friend_requests above; a stray delete must fail loudly rather
            // than silently orphan or prune result history.
            entity.HasOne<PlayerProfileRow>()
                .WithMany()
                .HasForeignKey(e => e.PlayerId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
