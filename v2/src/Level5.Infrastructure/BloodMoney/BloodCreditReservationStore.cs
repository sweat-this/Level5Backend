using Level5.Application.BloodMoney;
using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;
using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.BloodMoney;

public sealed class BloodCreditReservationStore(Level5V2DbContext db) : IBloodCreditReservationStore
{
    public async Task<BloodCreditReservation?> FindAsync(BloodMoneyChallengeId challengeId, PlayerId playerId, CancellationToken cancellationToken)
    {
        var row = db.Set<BloodCreditReservationRow>().Local.SingleOrDefault(row => row.ChallengeId == challengeId.Value && row.PlayerId == playerId.Value &&
            db.Entry(row).State is EntityState.Added or EntityState.Modified)
            ?? await db.Set<BloodCreditReservationRow>().AsNoTracking()
                .SingleOrDefaultAsync(row => row.ChallengeId == challengeId.Value && row.PlayerId == playerId.Value, cancellationToken);
        return row is null ? null : BloodCreditReservation.Rehydrate(new(row.ChallengeId), new(row.PlayerId), row.Amount,
            Enum.Parse<BloodCreditReservationStatus>(row.Status), new(row.ReserveTransactionId),
            row.ReleaseTransactionId.HasValue ? new BloodCreditTransactionId(row.ReleaseTransactionId.Value) : null,
            row.ReservedAt, row.ReleasedAt, row.ReleaseReason is null ? null : Enum.Parse<BloodCreditReleaseReason>(row.ReleaseReason), row.Revision);
    }

    public void Add(BloodCreditReservation reservation) => db.Add(ToRow(reservation));

    public void StageUpdate(BloodCreditReservation reservation, long expectedRevision)
    {
        if (reservation.Revision != checked(expectedRevision + 1))
            throw new InvalidBloodCreditsException("A reservation update must advance exactly one revision.");
        var row = db.Set<BloodCreditReservationRow>().Local.SingleOrDefault(row =>
            row.ChallengeId == reservation.ChallengeId.Value && row.PlayerId == reservation.PlayerId.Value);
        if (row is null)
        {
            row = ToRow(reservation);
            db.Attach(row);
        }
        var entry = db.Entry(row);
        var wasUnchanged = entry.State == EntityState.Unchanged;
        entry.CurrentValues.SetValues(ToRow(reservation));
        if (wasUnchanged)
        {
            entry.Property(value => value.Status).IsModified = true;
            entry.Property(value => value.ReleaseTransactionId).IsModified = true;
            entry.Property(value => value.ReleasedAt).IsModified = true;
            entry.Property(value => value.ReleaseReason).IsModified = true;
            entry.Property(value => value.Revision).IsModified = true;
            entry.Property(value => value.Revision).OriginalValue = expectedRevision;
        }
    }

    private static BloodCreditReservationRow ToRow(BloodCreditReservation reservation) => new()
    {
        ChallengeId = reservation.ChallengeId.Value, PlayerId = reservation.PlayerId.Value, Amount = reservation.Amount,
        Status = reservation.Status.ToString(), ReserveTransactionId = reservation.ReserveTransactionId.Value,
        ReleaseTransactionId = reservation.ReleaseTransactionId?.Value, ReservedAt = reservation.ReservedAt,
        ReleasedAt = reservation.ReleasedAt, ReleaseReason = reservation.ReleaseReason?.ToString(), Revision = reservation.Revision
    };
}

public sealed class BloodCreditReservationRow
{
    public Guid ChallengeId { get; set; }
    public Guid PlayerId { get; set; }
    public long Amount { get; set; }
    public string Status { get; set; } = null!;
    public Guid ReserveTransactionId { get; set; }
    public Guid? ReleaseTransactionId { get; set; }
    public DateTimeOffset ReservedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
    public long Revision { get; set; }
}
