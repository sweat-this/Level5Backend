using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;
using Xunit;

namespace Level5.Domain.Tests.BloodMoney;

public sealed class BloodMoneyChallengeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Gameplay = TimeSpan.FromHours(2);
    private static BloodMoneyChallenge Create(int count = 3) => BloodMoneyChallenge.Create(new(Guid.NewGuid()), PlayerId.New(), Guid.NewGuid(),
        Enumerable.Range(1, count - 1).Select(_ => PlayerId.New()), 30, "classic", 1, Now, Now.AddHours(1));
    private static BloodCreditReservation Hold(BloodMoneyChallenge challenge, PlayerId player, long amount = 30)
        => BloodCreditReservation.Reserve(challenge.Id, BloodCreditTransaction.Reserve(BloodCreditTransactionId.New(), player, amount, "test", Now));
    private static BloodCreditReservation[] Holds(BloodMoneyChallenge challenge)
        => challenge.Participants.Select(p => Hold(challenge, p.PlayerId)).ToArray();

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Structural_roster_is_ordered_immutable_and_has_no_four_seat_ceiling(int count)
    {
        var challenge = Create(count);
        Assert.Equal(1, challenge.Revision);
        Assert.Equal(Enumerable.Range(0, count), challenge.Participants.Select(p => p.SeatIndex));
        Assert.Equal(challenge.CreatorPlayerId, challenge.Participants[0].PlayerId);
        Assert.Equal(Now, challenge.Participants[0].AcceptedAt);
        Assert.All(challenge.Participants.Skip(1), p => Assert.Equal(BloodMoneyParticipantStatus.Invited, p.Status));
        Assert.Throws<NotSupportedException>(() => ((IList<BloodMoneyChallengeParticipant>)challenge.Participants).Clear());
        Assert.Equal(count, Rehydrate(challenge).Participants.Count);
    }

    [Fact]
    public void Partial_and_final_acceptance_require_exact_holds_and_advance_once()
    {
        var challenge = Create();
        var actor = challenge.Participants[1].PlayerId;
        Assert.Throws<InvalidBloodMoneyChallengeException>(() => challenge.Accept(actor, Now, Gameplay, []));
        Assert.Throws<InvalidBloodMoneyChallengeException>(() => challenge.Accept(actor, Now, Gameplay, [Hold(challenge, actor, 29)]));
        var holds = Holds(challenge);
        var partial = challenge.Accept(actor, Now.AddMinutes(1), Gameplay, holds);
        Assert.Equal(BloodMoneyChallengeStatus.PendingAcceptance, partial.Status);
        Assert.Equal(2, partial.Revision);
        Assert.Same(partial, partial.Accept(actor, Now.AddMinutes(2), Gameplay, []));
        var last = partial.Participants[2].PlayerId;
        Assert.Throws<InvalidBloodMoneyChallengeException>(() => partial.Accept(last, Now.AddMinutes(2), Gameplay, [holds[1], holds[2]]));
        var active = partial.Accept(last, Now.AddMinutes(2), Gameplay, holds);
        Assert.Equal(BloodMoneyChallengeStatus.Active, active.Status);
        Assert.Equal(3, active.Revision);
        Assert.Equal(Now.AddMinutes(2), active.ActivatedAt);
        Assert.Equal(Now.AddMinutes(2) + Gameplay, active.GameplayDeadlineAt);
        Assert.Same(active, active.Accept(last, Now.AddDays(1), Gameplay, []));
        Assert.Throws<InvalidBloodMoneyChallengeException>(() => active.Cancel(active.CreatorPlayerId, Now.AddDays(1)));
        Assert.Throws<InvalidBloodMoneyChallengeException>(() => active.Decline(last, Now.AddDays(1)));
        Assert.Throws<InvalidBloodMoneyChallengeException>(() => active.ExpirePending(Now.AddDays(1)));
        Assert.Equal(BloodMoneyChallengeStatus.Active, Rehydrate(active).Status);
    }

    [Fact]
    public void Terminal_transition_matrix_and_replay_attribution_are_strict()
    {
        var challenge = Create();
        var actor = challenge.Participants[1].PlayerId;
        var other = challenge.Participants[2].PlayerId;
        var declined = challenge.Decline(actor, Now.AddMinutes(1));
        Assert.Equal(BloodMoneyChallengeStatus.Declined, declined.Status);
        Assert.Equal(actor, declined.TerminalActorPlayerId);
        Assert.Equal(BloodMoneyParticipantStatus.Declined, declined.Participant(actor).Status);
        Assert.Same(declined, declined.Decline(actor, Now.AddDays(1)));
        Assert.Throws<InvalidBloodMoneyChallengeException>(() => declined.Decline(other, Now));
        var cancelled = challenge.Cancel(challenge.CreatorPlayerId, Now);
        Assert.Same(cancelled, cancelled.Cancel(challenge.CreatorPlayerId, Now.AddDays(1)));
        Assert.Throws<InvalidBloodMoneyChallengeException>(() => cancelled.Cancel(actor, Now));
        Assert.Throws<InvalidBloodMoneyChallengeException>(() => challenge.Cancel(actor, Now));
        Assert.Throws<InvalidBloodMoneyChallengeException>(() => challenge.Decline(challenge.CreatorPlayerId, Now));
        Assert.Throws<InvalidBloodMoneyChallengeException>(() => challenge.Accept(PlayerId.New(), Now, Gameplay, Holds(challenge)));
        foreach (var terminal in new[] { declined, cancelled, challenge.ExpirePending(challenge.AcceptanceDeadlineAt) })
        {
            Assert.Equal(2, terminal.Revision);
            Assert.Throws<InvalidBloodMoneyChallengeException>(() => terminal.Accept(actor, Now, Gameplay, Holds(challenge)));
            if (terminal.Status == BloodMoneyChallengeStatus.Cancelled)
                Assert.Same(terminal, terminal.Cancel(terminal.CreatorPlayerId, Now));
            else Assert.Throws<InvalidBloodMoneyChallengeException>(() => terminal.Cancel(terminal.CreatorPlayerId, Now));
            Assert.Equal(terminal.Status, Rehydrate(terminal).Status);
        }
    }

    [Fact]
    public void Acceptance_deadline_equality_expires_and_no_early_expiry_is_allowed()
    {
        var challenge = Create(); var deadline = challenge.AcceptanceDeadlineAt;
        Assert.Throws<InvalidBloodMoneyChallengeException>(() => challenge.ExpirePending(deadline.AddTicks(-1)));
        Assert.Throws<InvalidBloodMoneyChallengeException>(() => challenge.Accept(challenge.Participants[1].PlayerId, deadline, Gameplay, Holds(challenge)));
        Assert.Throws<InvalidBloodMoneyChallengeException>(() => challenge.Decline(challenge.Participants[1].PlayerId, deadline));
        Assert.Throws<InvalidBloodMoneyChallengeException>(() => challenge.Cancel(challenge.CreatorPlayerId, deadline));
        Assert.Throws<InvalidBloodMoneyChallengeException>(() => challenge.Accept(challenge.CreatorPlayerId, deadline, Gameplay, Holds(challenge)));
        var expired = challenge.ExpirePending(deadline);
        Assert.Equal(BloodMoneyChallengeStatus.Expired, expired.Status);
        Assert.Equal(deadline, expired.TerminalAt);
        Assert.Same(expired, expired.ExpirePending(deadline.AddDays(1)));
    }

    [Theory]
    [InlineData("duplicate-player")] [InlineData("duplicate-seat")] [InlineData("self")] [InlineData("empty-player")]
    [InlineData("creator-invited")] [InlineData("creator-seat")] [InlineData("accepted-no-time")]
    [InlineData("invited-time")] [InlineData("late-accepted")] [InlineData("pending-terminal")]
    [InlineData("pending-activation")] [InlineData("active-unaccepted")] [InlineData("zero-revision")]
    [InlineData("zero-stake")] [InlineData("rules")] [InlineData("version")] [InlineData("deadline")]
    [InlineData("empty-id")] [InlineData("empty-request")] [InlineData("status")] [InlineData("participant-status")]
    [InlineData("negative-seat")] [InlineData("one-player")]
    public void Rehydration_rejects_invalid_identity_roster_and_state_evidence(string invalid)
    {
        var c = Create(); var roster = c.Participants.ToArray();
        switch (invalid)
        {
            case "duplicate-player": roster[2] = roster[2] with { PlayerId = roster[1].PlayerId }; break;
            case "duplicate-seat": roster[2] = roster[2] with { SeatIndex = 1 }; break;
            case "self": roster[1] = roster[1] with { PlayerId = c.CreatorPlayerId }; break;
            case "empty-player": roster[1] = roster[1] with { PlayerId = default }; break;
            case "creator-invited": roster[0] = roster[0] with { Status = BloodMoneyParticipantStatus.Invited, AcceptedAt = null }; break;
            case "creator-seat": roster[0] = roster[0] with { SeatIndex = 5 }; break;
            case "accepted-no-time": roster[1] = roster[1] with { Status = BloodMoneyParticipantStatus.Accepted }; break;
            case "invited-time": roster[1] = roster[1] with { AcceptedAt = Now }; break;
            case "late-accepted": roster[1] = roster[1] with { Status = BloodMoneyParticipantStatus.Accepted, AcceptedAt = c.AcceptanceDeadlineAt }; break;
            case "participant-status": roster[1] = roster[1] with { Status = (BloodMoneyParticipantStatus)99 }; break;
            case "negative-seat": roster[1] = roster[1] with { SeatIndex = -1 }; break;
            case "one-player": roster = [roster[0]]; break;
        }
        Assert.Throws<InvalidBloodMoneyChallengeException>(() => BloodMoneyChallenge.Rehydrate(
            invalid == "empty-id" ? default : c.Id, c.CreatorPlayerId, invalid == "empty-request" ? Guid.Empty : c.ClientRequestId,
            invalid == "status" ? (BloodMoneyChallengeStatus)99 : invalid == "active-unaccepted" ? BloodMoneyChallengeStatus.Active : c.Status,
            invalid == "zero-stake" ? 0 : c.StakePerParticipant, invalid == "rules" ? " " : c.RulesetId, invalid == "version" ? 0 : c.RulesetVersion,
            c.CreatedAt, invalid == "deadline" ? Now : c.AcceptanceDeadlineAt, invalid == "pending-activation" ? Now : null,
            null, invalid == "pending-terminal" ? Now : null, null, invalid == "zero-revision" ? 0 : c.Revision, roster));
    }

    private static BloodMoneyChallenge Rehydrate(BloodMoneyChallenge c)
        => BloodMoneyChallenge.Rehydrate(c.Id, c.CreatorPlayerId, c.ClientRequestId, c.Status, c.StakePerParticipant,
            c.RulesetId, c.RulesetVersion, c.CreatedAt, c.AcceptanceDeadlineAt, c.ActivatedAt, c.GameplayDeadlineAt,
            c.TerminalAt, c.TerminalActorPlayerId, c.Revision, c.Participants.Reverse());
}
