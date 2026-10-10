using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;

namespace Level5.Application.BloodMoney;

/// <summary>All writes are staged in the same scope as financial mutations; none commits independently.</summary>
public interface IBloodMoneyChallengeStore
{
    Task<BloodMoneyChallenge?> FindAsync(BloodMoneyChallengeId id, CancellationToken cancellationToken);
    Task<BloodMoneyChallenge?> FindByCreateRequestAsync(PlayerId creator, Guid clientRequestId, CancellationToken cancellationToken);
    void Add(BloodMoneyChallenge challenge);
    void StageUpdate(BloodMoneyChallenge challenge, long expectedRevision);
}
