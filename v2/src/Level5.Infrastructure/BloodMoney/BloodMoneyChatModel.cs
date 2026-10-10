using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.BloodMoney;

internal static class BloodMoneyChatModel
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<BloodMoneyChatMessageRow>(entity =>
        {
            entity.ToTable("blood_money_chat_messages", table =>
            {
                table.HasCheckConstraint("CK_blood_chat_identity", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"ClientMessageId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint("CK_blood_chat_sequence", "\"Sequence\" > 0");
                table.HasCheckConstraint("CK_blood_chat_body", "octet_length(\"Body\") BETWEEN 1 AND 2000");
                table.HasCheckConstraint("CK_blood_chat_visibility", "\"Visibility\" IN ('Visible', 'Suppressed')");
            });
            entity.HasKey(row => row.Id);
            // Reports retain the exact message within its canonical challenge.
            entity.HasAlternateKey(row => new { row.ChallengeId, row.Id });
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property(row => row.Body).IsRequired();
            entity.Property(row => row.Visibility).HasMaxLength(16).IsRequired();
            entity.HasIndex(row => new { row.ChallengeId, row.Sequence }).IsUnique();
            entity.HasIndex(row => new { row.ChallengeId, row.SenderPlayerId, row.ClientMessageId }).IsUnique();
            entity.HasIndex(row => new { row.ChallengeId, row.SenderPlayerId, row.CreatedAt, row.Sequence });
            entity.HasOne<BloodMoneyChallengeParticipantRow>().WithMany()
                .HasForeignKey(row => new { row.ChallengeId, row.SenderPlayerId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<BloodMoneyChatReportRow>(entity =>
        {
            entity.ToTable("blood_money_chat_reports", table =>
            {
                table.HasCheckConstraint("CK_blood_chat_report_identity", "\"ReportId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint("CK_blood_chat_report_reason", "\"Reason\" IN ('Harassment', 'Hate', 'Threat', 'SexualContent', 'Spam', 'Other')");
            });
            entity.HasKey(row => row.ReportId);
            entity.Property(row => row.ReportId).ValueGeneratedNever();
            entity.Property(row => row.Reason).HasMaxLength(16).IsRequired();
            entity.HasIndex(row => new { row.ChallengeId, row.MessageId, row.ReporterPlayerId }).IsUnique();
            entity.HasOne<BloodMoneyChatMessageRow>().WithMany()
                .HasForeignKey(row => new { row.ChallengeId, row.MessageId })
                .HasPrincipalKey(row => new { row.ChallengeId, row.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<BloodMoneyChallengeParticipantRow>().WithMany()
                .HasForeignKey(row => new { row.ChallengeId, row.ReporterPlayerId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<BloodMoneyChatParticipantStateRow>(entity =>
        {
            entity.ToTable("blood_money_chat_participant_state", table =>
                table.HasCheckConstraint("CK_blood_chat_read_sequence", "\"LastReadSequence\" >= 0"));
            entity.HasKey(row => new { row.ChallengeId, row.PlayerId });
            entity.Property(row => row.ChallengeId).ValueGeneratedNever();
            entity.Property(row => row.PlayerId).ValueGeneratedNever();
            entity.Property(row => row.LastReadSequence).HasDefaultValue(0L);
            entity.Property(row => row.NotificationsMuted).HasDefaultValue(false);
            entity.HasOne<BloodMoneyChallengeParticipantRow>().WithMany()
                .HasForeignKey(row => new { row.ChallengeId, row.PlayerId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>Immutable intake only. The restrictive message reference retains original evidence;
/// MSG-005 owns protected operator handling, sanctions and retention.</summary>
public sealed class BloodMoneyChatReportRow
{
    public Guid ReportId { get; set; }
    public Guid ChallengeId { get; set; }
    public Guid MessageId { get; set; }
    public Guid ReporterPlayerId { get; set; }
    public string Reason { get; set; } = null!;
    public DateTimeOffset ReportedAt { get; set; }
}

public sealed class BloodMoneyChatMessageRow
{
    public Guid Id { get; set; }
    public Guid ChallengeId { get; set; }
    public long Sequence { get; set; }
    public Guid SenderPlayerId { get; set; }
    public Guid ClientMessageId { get; set; }
    public string Body { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public string Visibility { get; set; } = null!;
}

public sealed class BloodMoneyChatParticipantStateRow
{
    public Guid ChallengeId { get; set; }
    public Guid PlayerId { get; set; }
    public long LastReadSequence { get; set; }
    public bool NotificationsMuted { get; set; }
}
