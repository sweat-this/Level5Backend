using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.Persistence;

public sealed class Level5V2DbContext(DbContextOptions<Level5V2DbContext> options) : DbContext(options)
{
    public DbSet<AccountRow> Accounts => Set<AccountRow>();
    public DbSet<PlayerProfileRow> PlayerProfiles => Set<PlayerProfileRow>();
    public DbSet<FriendRequestRow> FriendRequests => Set<FriendRequestRow>();
    public DbSet<FriendshipRow> Friendships => Set<FriendshipRow>();
    public DbSet<VersusSeriesRow> VersusSeries => Set<VersusSeriesRow>();

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
        });

        modelBuilder.Entity<FriendshipRow>(entity =>
        {
            entity.ToTable("friendships");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.LowerPlayerId, e.UpperPlayerId }).IsUnique();
        });

        modelBuilder.Entity<VersusSeriesRow>(entity =>
        {
            entity.ToTable("competitive_series");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasMaxLength(24);
            entity.Property(e => e.StateJson).HasColumnType("jsonb");
            entity.HasIndex(e => new { e.OpponentId, e.Status });
            entity.HasIndex(e => new { e.ChallengerId, e.Status });
            // The optimistic-concurrency token: writes are conditioned on this column matching
            // the value that was read, via ExecuteUpdate's WHERE clause in VersusSeriesStore.
            entity.Property(e => e.Revision).IsConcurrencyToken();
        });
    }
}
