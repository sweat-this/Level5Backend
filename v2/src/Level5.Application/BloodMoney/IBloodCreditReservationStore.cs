using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;

namespace Level5.Application.BloodMoney;

public interface IBloodCreditReservationStore
{
    Task<BloodCreditReservation?> FindAsync(BloodMoneyChallengeId challengeId, PlayerId playerId, CancellationToken cancellationToken);
    void Add(BloodCreditReservation reservation);
    void StageUpdate(BloodCreditReservation reservation, long expectedRevision);
}
