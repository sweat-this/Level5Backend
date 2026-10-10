using Level5.Domain.Ids;

namespace Level5.Domain.BloodMoney;

public enum BloodMoneyChallengeStatus { PendingAcceptance, Active, Declined, Cancelled, Expired }
public enum BloodMoneyParticipantStatus { Invited, Accepted, Declined }

public sealed record BloodMoneyChallengeParticipant(PlayerId PlayerId, int SeatIndex,
    BloodMoneyParticipantStatus Status, DateTimeOffset? AcceptedAt);

public sealed class InvalidBloodMoneyChallengeException(string message) : Exception(message);

/// <summary>Immutable roster and lifecycle. Financial evidence is required for each new acceptance.</summary>
public sealed class BloodMoneyChallenge
{
    public const int RulesetIdMaxLength = 64;
    public BloodMoneyChallengeId Id { get; }
    public PlayerId CreatorPlayerId { get; }
    public Guid ClientRequestId { get; }
    public BloodMoneyChallengeStatus Status { get; }
    public long StakePerParticipant { get; }
    public string RulesetId { get; }
    public int RulesetVersion { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset AcceptanceDeadlineAt { get; }
    public DateTimeOffset? ActivatedAt { get; }
    public DateTimeOffset? GameplayDeadlineAt { get; }
    public DateTimeOffset? TerminalAt { get; }
    public PlayerId? TerminalActorPlayerId { get; }
    public long Revision { get; }
    public IReadOnlyList<BloodMoneyChallengeParticipant> Participants { get; }

    private BloodMoneyChallenge(BloodMoneyChallengeId id, PlayerId creatorPlayerId, Guid clientRequestId,
        BloodMoneyChallengeStatus status, long stakePerParticipant, string rulesetId, int rulesetVersion,
        DateTimeOffset createdAt, DateTimeOffset acceptanceDeadlineAt, DateTimeOffset? activatedAt,
        DateTimeOffset? gameplayDeadlineAt, DateTimeOffset? terminalAt, PlayerId? terminalActorPlayerId,
        long revision, IEnumerable<BloodMoneyChallengeParticipant> participants)
    {
        if (id.Value == Guid.Empty || creatorPlayerId.Value == Guid.Empty || clientRequestId == Guid.Empty ||
            stakePerParticipant <= 0 || revision <= 0 || !Enum.IsDefined(status) ||
            string.IsNullOrWhiteSpace(rulesetId) || rulesetId.Length > RulesetIdMaxLength || rulesetId != rulesetId.Trim() ||
            rulesetVersion <= 0 || acceptanceDeadlineAt <= createdAt)
            throw Invalid("Invalid challenge identity, stake, rules, revision, or acceptance deadline.");
        var roster = participants.OrderBy(p => p.SeatIndex).ToArray();
        if (roster.Length < 2 || roster.Any(p => p.PlayerId.Value == Guid.Empty || p.SeatIndex < 0 || !Enum.IsDefined(p.Status)) ||
            roster.Select(p => p.PlayerId).Distinct().Count() != roster.Length ||
            roster.Select(p => p.SeatIndex).Distinct().Count() != roster.Length ||
            roster[0].SeatIndex != 0 || roster[0].PlayerId != creatorPlayerId ||
            roster[0].Status != BloodMoneyParticipantStatus.Accepted || roster[0].AcceptedAt != createdAt)
            throw Invalid("A challenge requires a unique roster and accepted creator at seat zero.");
        if (roster.Any(p => p.Status == BloodMoneyParticipantStatus.Accepted
                ? p.AcceptedAt is null || p.AcceptedAt < createdAt || p.AcceptedAt >= acceptanceDeadlineAt
                : p.AcceptedAt is not null))
            throw Invalid("Participant acceptance evidence is incoherent.");
        var allAccepted = roster.All(p => p.Status == BloodMoneyParticipantStatus.Accepted);
        var declined = roster.Where(p => p.Status == BloodMoneyParticipantStatus.Declined).ToArray();
        if (status == BloodMoneyChallengeStatus.Active)
        {
            if (!allAccepted || activatedAt is null || activatedAt < createdAt || activatedAt >= acceptanceDeadlineAt ||
                gameplayDeadlineAt is null || gameplayDeadlineAt <= activatedAt || terminalAt is not null || terminalActorPlayerId is not null ||
                roster.Any(p => p.AcceptedAt > activatedAt))
                throw Invalid("An active challenge requires coherent activation and participant evidence.");
        }
        else
        {
            if (activatedAt is not null || gameplayDeadlineAt is not null || allAccepted)
                throw Invalid("Only an active challenge may carry activation evidence or a fully accepted roster.");
            if (status == BloodMoneyChallengeStatus.PendingAcceptance)
            {
                if (terminalAt is not null || terminalActorPlayerId is not null || declined.Length != 0)
                    throw Invalid("A pending challenge cannot carry terminal evidence.");
            }
            else
            {
                if (terminalAt is null || terminalAt < createdAt || roster.Any(p => p.AcceptedAt > terminalAt))
                    throw Invalid("A terminal challenge requires coherent terminal time.");
                if (status == BloodMoneyChallengeStatus.Expired
                    ? terminalAt < acceptanceDeadlineAt || terminalActorPlayerId is not null || declined.Length != 0
                    : terminalAt >= acceptanceDeadlineAt ||
                      (status == BloodMoneyChallengeStatus.Cancelled
                          ? terminalActorPlayerId != creatorPlayerId || declined.Length != 0
                          : declined.Length != 1 || declined[0].PlayerId != terminalActorPlayerId))
                    throw Invalid("Terminal status, actor, and deadline evidence disagree.");
            }
        }
        Id = id; CreatorPlayerId = creatorPlayerId; ClientRequestId = clientRequestId; Status = status;
        StakePerParticipant = stakePerParticipant; RulesetId = rulesetId; RulesetVersion = rulesetVersion;
        CreatedAt = createdAt; AcceptanceDeadlineAt = acceptanceDeadlineAt; ActivatedAt = activatedAt;
        GameplayDeadlineAt = gameplayDeadlineAt; TerminalAt = terminalAt; TerminalActorPlayerId = terminalActorPlayerId;
        Revision = revision; Participants = Array.AsReadOnly(roster);
    }

    public static BloodMoneyChallenge Create(BloodMoneyChallengeId id, PlayerId creator, Guid clientRequestId,
        IEnumerable<PlayerId> invitees, long stake, string rulesetId, int rulesetVersion,
        DateTimeOffset now, DateTimeOffset acceptanceDeadlineAt)
        => new(id, creator, clientRequestId, BloodMoneyChallengeStatus.PendingAcceptance, stake, rulesetId?.Trim()!, rulesetVersion,
            now, acceptanceDeadlineAt, null, null, null, null, 1,
            new[] { new BloodMoneyChallengeParticipant(creator, 0, BloodMoneyParticipantStatus.Accepted, now) }
                .Concat(invitees.Select((player, index) => new BloodMoneyChallengeParticipant(player, index + 1, BloodMoneyParticipantStatus.Invited, null))));

    public static BloodMoneyChallenge Rehydrate(BloodMoneyChallengeId id, PlayerId creator, Guid clientRequestId,
        BloodMoneyChallengeStatus status, long stake, string rulesetId, int rulesetVersion, DateTimeOffset createdAt,
        DateTimeOffset acceptanceDeadlineAt, DateTimeOffset? activatedAt, DateTimeOffset? gameplayDeadlineAt,
        DateTimeOffset? terminalAt, PlayerId? terminalActor, long revision, IEnumerable<BloodMoneyChallengeParticipant> participants)
        => new(id, creator, clientRequestId, status, stake, rulesetId, rulesetVersion, createdAt, acceptanceDeadlineAt,
            activatedAt, gameplayDeadlineAt, terminalAt, terminalActor, revision, participants);

    public bool HasSameCreateIntent(BloodMoneyChallenge other)
        => CreatorPlayerId == other.CreatorPlayerId && StakePerParticipant == other.StakePerParticipant &&
           RulesetId == other.RulesetId && RulesetVersion == other.RulesetVersion &&
           Participants.Select(p => (p.PlayerId, p.SeatIndex)).SequenceEqual(other.Participants.Select(p => (p.PlayerId, p.SeatIndex)));

    public BloodMoneyChallengeParticipant Participant(PlayerId actor)
        => Participants.SingleOrDefault(p => p.PlayerId == actor) ?? throw Invalid("The actor is not a participant.");

    public BloodMoneyChallenge Accept(PlayerId actor, DateTimeOffset now, TimeSpan gameplayWindow,
        IReadOnlyCollection<BloodCreditReservation> reservations)
    {
        var participant = Participant(actor);
        if (participant.Status == BloodMoneyParticipantStatus.Accepted && Status == BloodMoneyChallengeStatus.Active) return this;
        RequirePending(now);
        if (participant.Status == BloodMoneyParticipantStatus.Accepted) return this;
        if (participant.Status != BloodMoneyParticipantStatus.Invited || gameplayWindow <= TimeSpan.Zero)
            throw Invalid("Only an invited participant may accept with a positive gameplay window.");
        RequireFunding(actor, reservations);
        var roster = Participants.Select(p => p.PlayerId == actor ? p with { Status = BloodMoneyParticipantStatus.Accepted, AcceptedAt = now } : p).ToArray();
        var active = roster.All(p => p.Status == BloodMoneyParticipantStatus.Accepted);
        if (active) foreach (var player in roster) RequireFunding(player.PlayerId, reservations);
        return Copy(active ? BloodMoneyChallengeStatus.Active : Status, roster, active ? now : null,
            active ? now + gameplayWindow : null, null, null);
    }

    public BloodMoneyChallenge Decline(PlayerId actor, DateTimeOffset now)
    {
        var participant = Participant(actor);
        if (Status == BloodMoneyChallengeStatus.Declined && TerminalActorPlayerId == actor) return this;
        RequirePending(now);
        if (participant.Status != BloodMoneyParticipantStatus.Invited) throw Invalid("Only an invited participant may decline.");
        return Copy(BloodMoneyChallengeStatus.Declined,
            Participants.Select(p => p.PlayerId == actor ? p with { Status = BloodMoneyParticipantStatus.Declined } : p), null, null, now, actor);
    }

    public BloodMoneyChallenge Cancel(PlayerId actor, DateTimeOffset now)
    {
        if (actor != CreatorPlayerId) throw Invalid("Only the creator may cancel.");
        if (Status == BloodMoneyChallengeStatus.Cancelled) return this;
        RequirePending(now);
        return Copy(BloodMoneyChallengeStatus.Cancelled, Participants, null, null, now, actor);
    }

    public BloodMoneyChallenge ExpirePending(DateTimeOffset now)
    {
        if (Status == BloodMoneyChallengeStatus.Expired) return this;
        if (Status != BloodMoneyChallengeStatus.PendingAcceptance || now < AcceptanceDeadlineAt)
            throw Invalid("Only a pending challenge at its acceptance deadline may expire.");
        return Copy(BloodMoneyChallengeStatus.Expired, Participants, null, null, now, null);
    }

    private void RequirePending(DateTimeOffset now)
    {
        if (Status != BloodMoneyChallengeStatus.PendingAcceptance || now < CreatedAt || now >= AcceptanceDeadlineAt)
            throw Invalid("The challenge is not pending within its acceptance window.");
    }

    private void RequireFunding(PlayerId player, IReadOnlyCollection<BloodCreditReservation> reservations)
    {
        if (!reservations.Any(r => r.ChallengeId == Id && r.PlayerId == player &&
                r.Status == BloodCreditReservationStatus.Reserved && r.Amount == StakePerParticipant))
            throw Invalid("Every new acceptance and every active participant requires the exact reserved stake.");
    }

    private BloodMoneyChallenge Copy(BloodMoneyChallengeStatus status, IEnumerable<BloodMoneyChallengeParticipant> participants,
        DateTimeOffset? activatedAt, DateTimeOffset? gameplayDeadlineAt, DateTimeOffset? terminalAt, PlayerId? terminalActor)
        => new(Id, CreatorPlayerId, ClientRequestId, status, StakePerParticipant, RulesetId, RulesetVersion, CreatedAt,
            AcceptanceDeadlineAt, activatedAt, gameplayDeadlineAt, terminalAt, terminalActor, checked(Revision + 1), participants);

    private static InvalidBloodMoneyChallengeException Invalid(string message) => new(message);
}
