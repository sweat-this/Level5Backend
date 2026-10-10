using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;
using Level5.Application.Common;

namespace Level5.Application.BloodMoney;

/// <summary>Host-supplied rolling-window policy. No process-local accepted-message counters.</summary>
public sealed record BloodMoneyChatRatePolicy
{
    public int ShortLimit { get; }
    public TimeSpan ShortWindow { get; }
    public int LongLimit { get; }
    public TimeSpan LongWindow { get; }

    public BloodMoneyChatRatePolicy(int shortLimit, TimeSpan shortWindow, int longLimit, TimeSpan longWindow)
    {
        if (shortLimit <= 0 || longLimit <= 0 || shortWindow <= TimeSpan.Zero || longWindow < shortWindow)
            throw new ArgumentException("Chat rate limits and windows must be positive and ordered.");
        ShortLimit = shortLimit; ShortWindow = shortWindow; LongLimit = longLimit; LongWindow = longWindow;
    }
}

public sealed class BloodMoneyChatException(string code, string message, TimeSpan? retryAfter = null) : AppException(message)
{
    public override string Code { get; } = code;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed record BloodMoneyChatMessageView(Guid MessageId, BloodMoneyChallengeId ChallengeId, long Sequence,
    PlayerId SenderPlayerId, Guid ClientMessageId, string? Body, DateTimeOffset CreatedAt, BloodMoneyChatVisibility Visibility)
{
    public static BloodMoneyChatMessageView From(BloodMoneyChatMessage message)
        => new(message.MessageId, message.ChallengeId, message.Sequence, message.SenderPlayerId, message.ClientMessageId,
            message.Visibility == BloodMoneyChatVisibility.Visible ? message.Body : null, message.CreatedAt, message.Visibility);
}

public sealed record SendBloodMoneyChallengeMessageRequest(BloodMoneyChallengeId ChallengeId,
    PlayerId SenderPlayerId, Guid ClientMessageId, string Body);
public sealed record SendBloodMoneyChallengeMessageResult(BloodMoneyChatMessageView Message, bool Created);
public sealed record BloodMoneyChatParticipantState(long LastReadSequence, bool NotificationsMuted);

/// <summary>Owns atomic writes and consistent committed pages. Actor IDs are trusted application inputs.
/// Every operation reauthorizes against the canonical roster and activation evidence.</summary>
public interface IBloodMoneyChatStore
{
    Task<BloodMoneyChatPage> ListAsync(ListBloodMoneyChallengeMessagesRequest request, CancellationToken cancellationToken);
    Task<BloodMoneyChatReportAcceptance> ReportAsync(ReportBloodMoneyChatMessageRequest request, CancellationToken cancellationToken);
    Task<SendBloodMoneyChallengeMessageResult> SendAsync(SendBloodMoneyChallengeMessageRequest normalizedRequest,
        BloodMoneyChatRatePolicy ratePolicy, CancellationToken cancellationToken);
    Task<BloodMoneyChatParticipantState> GetParticipantStateAsync(BloodMoneyChallengeId challengeId, PlayerId actor, CancellationToken cancellationToken);
    Task<BloodMoneyChatParticipantState> AdvanceReadAsync(BloodMoneyChallengeId challengeId, PlayerId actor, long sequence, CancellationToken cancellationToken);
    Task<BloodMoneyChatParticipantState> SetNotificationsMutedAsync(BloodMoneyChallengeId challengeId, PlayerId actor, bool muted, CancellationToken cancellationToken);
}

public sealed class SendBloodMoneyChallengeMessageUseCase(IBloodMoneyChatStore store, BloodMoneyChatRatePolicy ratePolicy)
{
    public Task<SendBloodMoneyChallengeMessageResult> ExecuteAsync(SendBloodMoneyChallengeMessageRequest request, CancellationToken cancellationToken)
    {
        if (request.ClientMessageId == Guid.Empty)
            throw new BloodMoneyChatException("invalid_chat_message", "A nonempty client message ID is required.");
        string body;
        try { body = BloodMoneyChatText.Normalize(request.Body); }
        catch (InvalidBloodMoneyChatMessageException)
        { throw new BloodMoneyChatException("invalid_chat_message", "Message text is invalid."); }
        return store.SendAsync(request with { Body = body }, ratePolicy, cancellationToken);
    }
}
