using Level5.Domain.BloodMoney;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.BloodMoney;

internal static class BloodMoneyChallengeModel
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<BloodMoneyChallengeRow>(entity =>
        {
            entity.ToTable("blood_money_challenges", table =>
            {
                table.HasCheckConstraint("CK_blood_challenge_identity", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"ClientRequestId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint("CK_blood_challenge_values", "\"StakePerParticipant\" > 0 AND \"RulesetVersion\" > 0 AND \"Revision\" > 0 AND length(btrim(\"RulesetId\")) > 0 AND \"RulesetId\" = btrim(\"RulesetId\")");
                table.HasCheckConstraint("CK_blood_challenge_deadline", "\"AcceptanceDeadlineAt\" > \"CreatedAt\"");
                table.HasCheckConstraint("CK_blood_challenge_state", """
                    ("Status" = 'PendingAcceptance' AND "ActivatedAt" IS NULL AND "GameplayDeadlineAt" IS NULL AND "TerminalAt" IS NULL AND "TerminalActorPlayerId" IS NULL)
                    OR ("Status" = 'Active' AND "ActivatedAt" IS NOT NULL AND "ActivatedAt" >= "CreatedAt" AND "ActivatedAt" < "AcceptanceDeadlineAt"
                        AND "GameplayDeadlineAt" IS NOT NULL AND "GameplayDeadlineAt" > "ActivatedAt" AND "TerminalAt" IS NULL AND "TerminalActorPlayerId" IS NULL)
                    OR ("Status" IN ('Declined', 'Cancelled', 'Expired') AND "ActivatedAt" IS NULL AND "GameplayDeadlineAt" IS NULL
                        AND "TerminalAt" IS NOT NULL AND "TerminalAt" >= "CreatedAt"
                        AND (("Status" = 'Expired' AND "TerminalAt" >= "AcceptanceDeadlineAt" AND "TerminalActorPlayerId" IS NULL)
                            OR ("Status" IN ('Declined', 'Cancelled') AND "TerminalAt" < "AcceptanceDeadlineAt" AND "TerminalActorPlayerId" IS NOT NULL
                                AND ("Status" <> 'Cancelled' OR "TerminalActorPlayerId" = "CreatorPlayerId"))))
                    """);
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property(row => row.Status).HasMaxLength(24);
            entity.Property(row => row.RulesetId).HasMaxLength(BloodMoneyChallenge.RulesetIdMaxLength);
            entity.Property(row => row.Revision).IsConcurrencyToken();
            entity.HasIndex(row => new { row.CreatorPlayerId, row.ClientRequestId }).IsUnique();
            entity.HasOne<PlayerProfileRow>().WithMany().HasForeignKey(row => row.CreatorPlayerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlayerProfileRow>().WithMany().HasForeignKey(row => row.TerminalActorPlayerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(row => row.Participants).WithOne().HasForeignKey(row => row.ChallengeId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<BloodMoneyChallengeParticipantRow>(entity =>
        {
            entity.ToTable("blood_money_challenge_participants", table =>
            {
                // There is deliberately no upper bound: current 2..4 admission is Application policy.
                table.HasCheckConstraint("CK_blood_participant_seat", "\"SeatIndex\" >= 0");
                table.HasCheckConstraint("CK_blood_participant_state", "(\"Status\" = 'Accepted' AND \"AcceptedAt\" IS NOT NULL) OR (\"Status\" IN ('Invited', 'Declined') AND \"AcceptedAt\" IS NULL)");
            });
            entity.HasKey(row => new { row.ChallengeId, row.PlayerId });
            entity.Property(row => row.ChallengeId).ValueGeneratedNever();
            entity.Property(row => row.PlayerId).ValueGeneratedNever();
            entity.Property(row => row.Status).HasMaxLength(16);
            entity.HasIndex(row => new { row.ChallengeId, row.SeatIndex }).IsUnique();
            entity.HasOne<PlayerProfileRow>().WithMany().HasForeignKey(row => row.PlayerId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
