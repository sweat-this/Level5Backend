using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;

namespace Level5.Application.BloodMoney;

/// <summary>Supplied by the owning application; no product durations are assumed by Domain.</summary>
public sealed record BloodMoneyChallengeTimingPolicy
{
    public TimeSpan AcceptanceWindow { get; }
    public TimeSpan GameplayWindow { get; }

    public BloodMoneyChallengeTimingPolicy(TimeSpan acceptanceWindow, TimeSpan gameplayWindow)
    {
        if (acceptanceWindow <= TimeSpan.Zero || gameplayWindow <= TimeSpan.Zero)
            throw new ValidationFailedException("Challenge timing windows must be positive.");
        AcceptanceWindow = acceptanceWindow; GameplayWindow = gameplayWindow;
    }
}

public sealed record CreateBloodMoneyChallengeRequest(PlayerId CreatorPlayerId, IReadOnlyList<PlayerId> Invitees,
    long StakePerParticipant, string RulesetId, int RulesetVersion, Guid ClientRequestId);

public sealed record BloodMoneyChallengeParticipantView(PlayerId PlayerId, int SeatIndex,
    BloodMoneyParticipantStatus Status, DateTimeOffset? AcceptedAt);

public sealed record BloodMoneyChallengeView(BloodMoneyChallengeId ChallengeId, PlayerId CreatorPlayerId,
    BloodMoneyChallengeStatus Status, long StakePerParticipant, string RulesetId, int RulesetVersion,
    DateTimeOffset CreatedAt, DateTimeOffset AcceptanceDeadlineAt, DateTimeOffset? ActivatedAt,
    DateTimeOffset? GameplayDeadlineAt, DateTimeOffset? TerminalAt, long Revision,
    IReadOnlyList<BloodMoneyChallengeParticipantView> Participants)
{
    internal static BloodMoneyChallengeView From(BloodMoneyChallenge challenge)
        => new(challenge.Id, challenge.CreatorPlayerId, challenge.Status, challenge.StakePerParticipant,
            challenge.RulesetId, challenge.RulesetVersion, challenge.CreatedAt, challenge.AcceptanceDeadlineAt,
            challenge.ActivatedAt, challenge.GameplayDeadlineAt, challenge.TerminalAt, challenge.Revision,
            Array.AsReadOnly(challenge.Participants.Select(p => new BloodMoneyChallengeParticipantView(p.PlayerId, p.SeatIndex, p.Status, p.AcceptedAt)).ToArray()));
}

public sealed class CreateBloodMoneyChallengeUseCase(IBloodMoneyChallengeStore challenges, IFriendshipStore friendships,
    BloodCreditReservationMutator financial, IUnitOfWork unitOfWork, IClock clock, BloodMoneyChallengeTimingPolicy timing)
{
    public async Task<BloodMoneyChallengeView> ExecuteAsync(CreateBloodMoneyChallengeRequest request, CancellationToken cancellationToken)
    {
        if (request.Invitees is null || request.Invitees.Count is < 1 or > 3)
            throw new ValidationFailedException("Current friend challenges require two to four participants including the creator.");
        var now = clock.UtcNow;
        var challenge = BloodMoneyChallenge.Create(new(Guid.NewGuid()), request.CreatorPlayerId, request.ClientRequestId,
            request.Invitees, request.StakePerParticipant, request.RulesetId, request.RulesetVersion, now, now + timing.AcceptanceWindow);
        var existing = await challenges.FindByCreateRequestAsync(request.CreatorPlayerId, request.ClientRequestId, cancellationToken);
        if (existing is not null)
            return Replay(existing, challenge);
        foreach (var invitee in challenge.Participants.Skip(1))
            if (!await friendships.AreFriendsAsync(challenge.CreatorPlayerId, invitee.PlayerId, cancellationToken))
                throw new FriendshipRequiredException("Every invitee must be an accepted friend of the creator.");
        try
        {
            await financial.StageReserveAsync(new(BloodCreditTransactionId.New(), challenge.Id, challenge.CreatorPlayerId, challenge.StakePerParticipant), cancellationToken);
        }
        catch (InsufficientCreditsException)
        {
            // A duplicate may have committed after the initial lookup and consumed the remaining credits.
            // Insufficient funding fails before the reservation mutator stages any changes.
            existing = await challenges.FindByCreateRequestAsync(request.CreatorPlayerId, request.ClientRequestId, cancellationToken);
            if (existing is not null) return Replay(existing, challenge);
            throw;
        }
        challenges.Add(challenge);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return BloodMoneyChallengeView.From(challenge);
    }

    private static BloodMoneyChallengeView Replay(BloodMoneyChallenge existing, BloodMoneyChallenge candidate)
    {
        if (!existing.HasSameCreateIntent(candidate)) throw new ConflictException("The create request ID was used for another challenge intent.");
        return BloodMoneyChallengeView.From(existing);
    }
}

/// <summary>One lifecycle path shares a single commit with the existing financial staging primitives.
/// A failed scope must be discarded. Retrying means loading again in a fresh scope.</summary>
public sealed class BloodMoneyChallengeLifecycle(IBloodMoneyChallengeStore challenges, IBloodCreditReservationStore reservations,
    BloodCreditReservationMutator financial, IUnitOfWork unitOfWork, IClock clock, BloodMoneyChallengeTimingPolicy timing)
{
    internal async Task<BloodMoneyChallengeView> AcceptAsync(BloodMoneyChallengeId id, PlayerId actor, CancellationToken ct)
    {
        var challenge = await LoadForParticipant(id, actor, ct);
        var now = clock.UtcNow;
        if (IsDue(challenge, now)) return await Expire(challenge, now, ct);
        var participant = challenge.Participant(actor);
        if (participant.Status == BloodMoneyParticipantStatus.Accepted &&
            challenge.Status is BloodMoneyChallengeStatus.PendingAcceptance or BloodMoneyChallengeStatus.Active)
            return BloodMoneyChallengeView.From(challenge);
        if (challenge.Status != BloodMoneyChallengeStatus.PendingAcceptance || participant.Status != BloodMoneyParticipantStatus.Invited)
            throw new ConflictException("This participant cannot accept the challenge in its current state.");
        await financial.StageReserveAsync(new(BloodCreditTransactionId.New(), id, actor, challenge.StakePerParticipant), ct);
        var holds = new List<BloodCreditReservation>();
        foreach (var player in challenge.Participants)
        {
            var hold = await reservations.FindAsync(id, player.PlayerId, ct);
            if (hold is not null) holds.Add(hold);
        }
        var updated = challenge.Accept(actor, now, timing.GameplayWindow, holds);
        return await Save(challenge, updated, ct);
    }

    internal async Task<BloodMoneyChallengeView> DeclineAsync(BloodMoneyChallengeId id, PlayerId actor, CancellationToken ct)
    {
        var challenge = await LoadForParticipant(id, actor, ct);
        var now = clock.UtcNow;
        if (IsDue(challenge, now)) return await Expire(challenge, now, ct);
        return await SaveTerminal(challenge, challenge.Decline(actor, now), BloodCreditReleaseReason.Declined, ct);
    }

    internal async Task<BloodMoneyChallengeView> CancelAsync(BloodMoneyChallengeId id, PlayerId actor, CancellationToken ct)
    {
        var challenge = await LoadForParticipant(id, actor, ct);
        if (challenge.CreatorPlayerId != actor) throw new ConflictException("Only the creator may cancel.");
        var now = clock.UtcNow;
        if (IsDue(challenge, now)) return await Expire(challenge, now, ct);
        return await SaveTerminal(challenge, challenge.Cancel(actor, now), BloodCreditReleaseReason.Cancelled, ct);
    }

    internal async Task<BloodMoneyChallengeView> ExpirePendingAsync(BloodMoneyChallengeId id, CancellationToken ct)
    {
        var challenge = await challenges.FindAsync(id, ct) ?? throw Missing();
        return await Expire(challenge, clock.UtcNow, ct);
    }

    private Task<BloodMoneyChallengeView> Expire(BloodMoneyChallenge challenge, DateTimeOffset now, CancellationToken ct)
        => SaveTerminal(challenge, challenge.ExpirePending(now), BloodCreditReleaseReason.PendingExpired, ct);

    private async Task<BloodMoneyChallengeView> SaveTerminal(BloodMoneyChallenge original, BloodMoneyChallenge updated,
        BloodCreditReleaseReason reason, CancellationToken ct)
    {
        if (updated.Revision == original.Revision) return BloodMoneyChallengeView.From(original);
        foreach (var participant in original.Participants)
        {
            var reservation = await reservations.FindAsync(original.Id, participant.PlayerId, ct);
            if (reservation?.Status == BloodCreditReservationStatus.Reserved)
                await financial.StageReleaseAsync(new(BloodCreditTransactionId.New(), original.Id, participant.PlayerId, reason), ct);
        }
        return await Save(original, updated, ct);
    }

    private async Task<BloodMoneyChallengeView> Save(BloodMoneyChallenge original, BloodMoneyChallenge updated, CancellationToken ct)
    {
        challenges.StageUpdate(updated, original.Revision);
        await unitOfWork.SaveChangesAsync(ct);
        return BloodMoneyChallengeView.From(updated);
    }

    private async Task<BloodMoneyChallenge> LoadForParticipant(BloodMoneyChallengeId id, PlayerId actor, CancellationToken ct)
    {
        var challenge = await challenges.FindAsync(id, ct);
        if (challenge is null || !challenge.Participants.Any(p => p.PlayerId == actor)) throw Missing();
        return challenge;
    }

    private static bool IsDue(BloodMoneyChallenge challenge, DateTimeOffset now)
        => challenge.Status == BloodMoneyChallengeStatus.PendingAcceptance && now >= challenge.AcceptanceDeadlineAt;
    private static NotFoundException Missing() => new("Challenge not found.");
}

public sealed class AcceptBloodMoneyChallengeUseCase(BloodMoneyChallengeLifecycle lifecycle)
{
    public Task<BloodMoneyChallengeView> ExecuteAsync(BloodMoneyChallengeId id, PlayerId actor, CancellationToken ct)
        => lifecycle.AcceptAsync(id, actor, ct);
}

public sealed class DeclineBloodMoneyChallengeUseCase(BloodMoneyChallengeLifecycle lifecycle)
{
    public Task<BloodMoneyChallengeView> ExecuteAsync(BloodMoneyChallengeId id, PlayerId actor, CancellationToken ct)
        => lifecycle.DeclineAsync(id, actor, ct);
}

public sealed class CancelBloodMoneyChallengeUseCase(BloodMoneyChallengeLifecycle lifecycle)
{
    public Task<BloodMoneyChallengeView> ExecuteAsync(BloodMoneyChallengeId id, PlayerId actor, CancellationToken ct)
        => lifecycle.CancelAsync(id, actor, ct);
}

/// <summary>Trusted pending-expiry entry point for the future scanner; never adjudicates an active game.</summary>
public sealed class ExpirePendingBloodMoneyChallengeUseCase(BloodMoneyChallengeLifecycle lifecycle)
{
    public Task<BloodMoneyChallengeView> ExecuteAsync(BloodMoneyChallengeId id, CancellationToken ct)
        => lifecycle.ExpirePendingAsync(id, ct);
}

public sealed class GetBloodMoneyChallengeUseCase(IBloodMoneyChallengeStore challenges)
{
    public async Task<BloodMoneyChallengeView> ExecuteAsync(BloodMoneyChallengeId id, PlayerId actor, CancellationToken ct)
    {
        var challenge = await challenges.FindAsync(id, ct);
        if (challenge is null || !challenge.Participants.Any(p => p.PlayerId == actor)) throw new NotFoundException("Challenge not found.");
        return BloodMoneyChallengeView.From(challenge);
    }
}
