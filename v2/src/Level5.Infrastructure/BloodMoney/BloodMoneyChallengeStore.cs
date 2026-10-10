using Level5.Application.BloodMoney;
using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;
using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.BloodMoney;

public sealed class BloodMoneyChallengeStore(Level5V2DbContext db) : IBloodMoneyChallengeStore
{
    // One tracked query captures challenge/roster together and preserves the originally loaded
    // Revision through StageUpdate. Re-reading in this scope must not refresh its baseline.
    public async Task<BloodMoneyChallenge?> FindAsync(BloodMoneyChallengeId id, CancellationToken cancellationToken)
        => FromRow(await Query().SingleOrDefaultAsync(row => row.Id == id.Value, cancellationToken));

    public async Task<BloodMoneyChallenge?> FindByCreateRequestAsync(PlayerId creator, Guid clientRequestId, CancellationToken cancellationToken)
        => FromRow(await Query().SingleOrDefaultAsync(row => row.CreatorPlayerId == creator.Value && row.ClientRequestId == clientRequestId, cancellationToken));

    private IQueryable<BloodMoneyChallengeRow> Query()
        => db.Set<BloodMoneyChallengeRow>().AsTracking().Include(row => row.Participants).AsSingleQuery();

    public void Add(BloodMoneyChallenge challenge) => db.Add(ToRow(challenge));

    public void StageUpdate(BloodMoneyChallenge challenge, long expectedRevision)
    {
        var row = db.Set<BloodMoneyChallengeRow>().Local.SingleOrDefault(row => row.Id == challenge.Id.Value)
            ?? throw new InvalidOperationException("Load the challenge in this scope before staging its update.");
        var entry = db.Entry(row);
        if (entry.State != EntityState.Unchanged || entry.Property(row => row.Revision).OriginalValue != expectedRevision ||
            challenge.Revision != checked(expectedRevision + 1))
            throw new InvalidOperationException("A lifecycle update must advance the originally loaded revision exactly once.");
        if (!FromRow(row)!.HasSameCreateIntent(challenge) || row.ClientRequestId != challenge.ClientRequestId ||
            row.CreatedAt != challenge.CreatedAt || row.AcceptanceDeadlineAt != challenge.AcceptanceDeadlineAt)
            throw new InvalidOperationException("A lifecycle update cannot change the immutable challenge request, roster, or acceptance window.");
        // Only mutable lifecycle fields are written. Identity, request fingerprint and roster stay frozen.
        row.Status = challenge.Status.ToString(); row.ActivatedAt = challenge.ActivatedAt;
        row.GameplayDeadlineAt = challenge.GameplayDeadlineAt; row.TerminalAt = challenge.TerminalAt;
        row.TerminalActorPlayerId = challenge.TerminalActorPlayerId?.Value; row.Revision = challenge.Revision;
        foreach (var participant in challenge.Participants)
        {
            var persisted = row.Participants.Single(p => p.PlayerId == participant.PlayerId.Value && p.SeatIndex == participant.SeatIndex);
            var status = participant.Status.ToString();
            if (persisted.Status == status && persisted.AcceptedAt == participant.AcceptedAt) continue;
            persisted.Status = status; persisted.AcceptedAt = participant.AcceptedAt;
            // Write the pair even when AcceptedAt remains null. EF may update participants before
            // the parent revision check; a stale decline must not inherit a competing acceptance's
            // timestamp and fail a CHECK before reaching the canonical concurrency conflict.
            var participantEntry = db.Entry(persisted);
            participantEntry.Property(p => p.Status).IsModified = true;
            participantEntry.Property(p => p.AcceptedAt).IsModified = true;
        }
        // DetectChanges retains entry.OriginalValues, including the loaded concurrency token.
    }

    private static BloodMoneyChallenge? FromRow(BloodMoneyChallengeRow? row)
        => row is null ? null : BloodMoneyChallenge.Rehydrate(new(row.Id), new(row.CreatorPlayerId), row.ClientRequestId,
            Enum.Parse<BloodMoneyChallengeStatus>(row.Status), row.StakePerParticipant, row.RulesetId, row.RulesetVersion,
            row.CreatedAt, row.AcceptanceDeadlineAt, row.ActivatedAt, row.GameplayDeadlineAt, row.TerminalAt,
            row.TerminalActorPlayerId is { } actor ? new PlayerId(actor) : null, row.Revision,
            row.Participants.Select(p => new BloodMoneyChallengeParticipant(new(p.PlayerId), p.SeatIndex,
                Enum.Parse<BloodMoneyParticipantStatus>(p.Status), p.AcceptedAt)));

    private static BloodMoneyChallengeRow ToRow(BloodMoneyChallenge challenge) => new()
    {
        Id = challenge.Id.Value, CreatorPlayerId = challenge.CreatorPlayerId.Value, ClientRequestId = challenge.ClientRequestId,
        Status = challenge.Status.ToString(), StakePerParticipant = challenge.StakePerParticipant,
        RulesetId = challenge.RulesetId, RulesetVersion = challenge.RulesetVersion, CreatedAt = challenge.CreatedAt,
        AcceptanceDeadlineAt = challenge.AcceptanceDeadlineAt, ActivatedAt = challenge.ActivatedAt,
        GameplayDeadlineAt = challenge.GameplayDeadlineAt, TerminalAt = challenge.TerminalAt,
        TerminalActorPlayerId = challenge.TerminalActorPlayerId?.Value, Revision = challenge.Revision,
        Participants = challenge.Participants.Select(p => new BloodMoneyChallengeParticipantRow
        {
            ChallengeId = challenge.Id.Value, PlayerId = p.PlayerId.Value, SeatIndex = p.SeatIndex,
            Status = p.Status.ToString(), AcceptedAt = p.AcceptedAt
        }).ToList()
    };
}

public sealed class BloodMoneyChallengeRow
{
    public Guid Id { get; set; }
    public Guid CreatorPlayerId { get; set; }
    public Guid ClientRequestId { get; set; }
    public string Status { get; set; } = null!;
    public long StakePerParticipant { get; set; }
    public string RulesetId { get; set; } = null!;
    public int RulesetVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset AcceptanceDeadlineAt { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? GameplayDeadlineAt { get; set; }
    public DateTimeOffset? TerminalAt { get; set; }
    public Guid? TerminalActorPlayerId { get; set; }
    public long Revision { get; set; }
    public List<BloodMoneyChallengeParticipantRow> Participants { get; set; } = [];
}

public sealed class BloodMoneyChallengeParticipantRow
{
    public Guid ChallengeId { get; set; }
    public Guid PlayerId { get; set; }
    public int SeatIndex { get; set; }
    public string Status { get; set; } = null!;
    public DateTimeOffset? AcceptedAt { get; set; }
}
