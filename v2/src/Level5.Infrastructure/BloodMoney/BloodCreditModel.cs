using Level5.Domain.BloodMoney;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.BloodMoney;

internal static class BloodCreditModel
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<BloodCreditAccountRow>(entity =>
        {
            entity.ToTable("blood_money_credit_accounts", table =>
            {
                table.HasCheckConstraint("CK_blood_credit_balance", "\"AvailableBalance\" >= 0");
                table.HasCheckConstraint("CK_blood_credit_revision", "\"Revision\" >= 0");
            });
            entity.HasKey(row => row.PlayerId);
            entity.Property(row => row.PlayerId).ValueGeneratedNever();
            entity.Property(row => row.Revision).IsConcurrencyToken();
            entity.HasOne<PlayerProfileRow>().WithMany().HasForeignKey(row => row.PlayerId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<BloodCreditTransactionRow>(entity =>
        {
            entity.ToTable("blood_money_credit_transactions", table =>
            {
                table.HasCheckConstraint("CK_blood_credit_delta", "\"PlayerDelta\" <> 0 AND \"PlayerDelta\" <> '-9223372036854775808'::bigint");
                table.HasCheckConstraint("CK_blood_credit_kind", "(\"Kind\" IN ('Issuance', 'Release') AND \"PlayerDelta\" > 0) OR (\"Kind\" IN ('Spend', 'Reserve') AND \"PlayerDelta\" < 0) OR \"Kind\" = 'Correction'");
                table.HasCheckConstraint("CK_blood_credit_reference", "length(btrim(\"ReferenceCode\")) > 0");
                table.HasCheckConstraint("CK_blood_credit_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property(row => row.Kind).HasMaxLength(16);
            entity.Property(row => row.ReferenceCode).HasMaxLength(BloodCreditTransaction.ReferenceMaxLength);
            entity.HasIndex(row => new { row.SubjectPlayerId, row.CreatedAt, row.Id });
            entity.HasOne<PlayerProfileRow>().WithMany().HasForeignKey(row => row.SubjectPlayerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(row => row.Postings).WithOne().HasForeignKey(row => row.TransactionId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<BloodCreditPostingRow>(entity =>
        {
            entity.ToTable("blood_money_credit_postings", table =>
            {
                table.HasCheckConstraint("CK_blood_credit_posting_amount", "\"Amount\" <> 0");
                table.HasCheckConstraint("CK_blood_credit_posting_owner", "(\"PostingOwner\" = 'Player' AND \"PlayerId\" IS NOT NULL AND \"LineNumber\" = 1) OR (\"PostingOwner\" = 'Treasury' AND \"PlayerId\" IS NULL AND \"LineNumber\" = 2)");
            });
            entity.HasKey(row => new { row.TransactionId, row.LineNumber });
            entity.Property(row => row.PostingOwner).HasMaxLength(16);
            entity.HasIndex(row => row.PlayerId);
            entity.HasOne<PlayerProfileRow>().WithMany().HasForeignKey(row => row.PlayerId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<BloodCreditReservationRow>(entity =>
        {
            entity.ToTable("blood_money_credit_reservations", table =>
            {
                table.HasCheckConstraint("CK_blood_reservation_identity", "\"ChallengeId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"PlayerId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint("CK_blood_reservation_amount", "\"Amount\" > 0");
                table.HasCheckConstraint("CK_blood_reservation_revision", "\"Revision\" > 0");
                table.HasCheckConstraint("CK_blood_reservation_state", """
                    ("Status" = 'Reserved' AND "ReleaseTransactionId" IS NULL AND "ReleasedAt" IS NULL AND "ReleaseReason" IS NULL)
                    OR ("Status" = 'Released' AND "ReleaseTransactionId" IS NOT NULL AND "ReleasedAt" IS NOT NULL
                        AND "ReleasedAt" >= "ReservedAt" AND "ReleaseReason" IS NOT NULL
                        AND "ReleaseReason" IN ('Declined', 'Cancelled', 'PendingExpired') AND "ReleaseTransactionId" <> "ReserveTransactionId")
                    """);
            });
            entity.HasKey(row => new { row.ChallengeId, row.PlayerId });
            entity.Property(row => row.ChallengeId).ValueGeneratedNever();
            entity.Property(row => row.PlayerId).ValueGeneratedNever();
            entity.Property(row => row.Status).HasMaxLength(16);
            entity.Property(row => row.ReleaseReason).HasMaxLength(16);
            entity.Property(row => row.Revision).IsConcurrencyToken();
            entity.HasIndex(row => row.ReserveTransactionId).IsUnique();
            entity.HasIndex(row => row.ReleaseTransactionId).IsUnique().HasFilter("\"ReleaseTransactionId\" IS NOT NULL");
            entity.HasOne<PlayerProfileRow>().WithMany().HasForeignKey(row => row.PlayerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<BloodCreditTransactionRow>().WithMany().HasForeignKey(row => row.ReserveTransactionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<BloodCreditTransactionRow>().WithMany().HasForeignKey(row => row.ReleaseTransactionId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
