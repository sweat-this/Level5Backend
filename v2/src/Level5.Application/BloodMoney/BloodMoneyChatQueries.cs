using Level5.Domain.Ids;
using Level5.Domain.BloodMoney;

namespace Level5.Application.BloodMoney;

public enum BloodMoneyChatDirection { Older = 1, Resume = 2 }
public sealed record BloodMoneyChatCursor(BloodMoneyChatDirection Direction, BloodMoneyChallengeId ChallengeId,
    long BoundarySequence, long SnapshotUpperBound);

/// <summary>Infrastructure authenticates versioned, purpose-separated, challenge-scoped tokens.</summary>
public interface IBloodMoneyChatCursorCodec
{
    string Encode(BloodMoneyChatCursor cursor);
    BloodMoneyChatCursor Decode(string token, BloodMoneyChallengeId challengeId);
}

public sealed record ListBloodMoneyChallengeMessagesRequest(BloodMoneyChallengeId ChallengeId, PlayerId Actor,
    string? Limit = null, string? Cursor = null);
public sealed record BloodMoneyChatPage(IReadOnlyList<BloodMoneyChatMessageView> Items, string? OlderCursor,
    string ResumeCursor, long LatestSequence, long LastReadSequence, bool IsReadOnly, bool NotificationsMuted);

public enum BloodMoneyChatReportReason { Harassment, Hate, Threat, SexualContent, Spam, Other }
public sealed record ReportBloodMoneyChatMessageRequest(BloodMoneyChallengeId ChallengeId, PlayerId Reporter,
    Guid MessageId, BloodMoneyChatReportReason Reason);
public sealed record BloodMoneyChatReportAcceptance(Guid ReportId);

public sealed class ListBloodMoneyChallengeMessagesUseCase(IBloodMoneyChatStore store)
{
    public Task<BloodMoneyChatPage> ExecuteAsync(ListBloodMoneyChallengeMessagesRequest request, CancellationToken cancellationToken)
        => store.ListAsync(request, cancellationToken);
}

public sealed class AdvanceBloodMoneyChatReadPositionUseCase(IBloodMoneyChatStore store)
{
    public Task<BloodMoneyChatParticipantState> ExecuteAsync(BloodMoneyChallengeId challengeId, PlayerId actor,
        long sequence, CancellationToken cancellationToken) => store.AdvanceReadAsync(challengeId, actor, sequence, cancellationToken);
}

public sealed class SetBloodMoneyChatNotificationsMutedUseCase(IBloodMoneyChatStore store)
{
    public Task<BloodMoneyChatParticipantState> ExecuteAsync(BloodMoneyChallengeId challengeId, PlayerId actor,
        bool muted, CancellationToken cancellationToken) => store.SetNotificationsMutedAsync(challengeId, actor, muted, cancellationToken);
}

public sealed class ReportBloodMoneyChatMessageUseCase(IBloodMoneyChatStore store)
{
    public Task<BloodMoneyChatReportAcceptance> ExecuteAsync(ReportBloodMoneyChatMessageRequest request, CancellationToken cancellationToken)
        => store.ReportAsync(request, cancellationToken);
}
