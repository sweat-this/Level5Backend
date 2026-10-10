using System.Globalization;
using System.Text;
using Level5.Domain.Ids;

namespace Level5.Domain.BloodMoney;

public enum BloodMoneyChatVisibility { Visible, Suppressed }

public sealed class InvalidBloodMoneyChatMessageException(string message) : Exception(message);

/// <summary>The single normalization algorithm used for persistence and retry comparison.</summary>
public static class BloodMoneyChatText
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Normalize(string? body)
    {
        if (body is null) throw Invalid();
        try
        {
            // Validate before Normalize/EnumerateRunes, which must never replace malformed UTF-16.
            _ = StrictUtf8.GetByteCount(body);
            var normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                .Normalize(NormalizationForm.FormC);
            foreach (var rune in normalized.EnumerateRunes())
                if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control && rune.Value != '\n')
                    throw Invalid();
            normalized = normalized.Trim();
            if (normalized.Length == 0 || normalized.EnumerateRunes().Count() > 500 || StrictUtf8.GetByteCount(normalized) > 2000)
                throw Invalid();
            return normalized;
        }
        catch (EncoderFallbackException) { throw Invalid(); }
    }

    private static InvalidBloodMoneyChatMessageException Invalid()
        => new("Message must contain valid normalized text within the chat limits.");
}

/// <summary>Accepted content is immutable. Visibility is reserved for authorized moderation.</summary>
public sealed class BloodMoneyChatMessage
{
    public Guid MessageId { get; }
    public BloodMoneyChallengeId ChallengeId { get; }
    public long Sequence { get; }
    public PlayerId SenderPlayerId { get; }
    public Guid ClientMessageId { get; }
    public string Body { get; }
    public DateTimeOffset CreatedAt { get; }
    public BloodMoneyChatVisibility Visibility { get; }

    public BloodMoneyChatMessage(Guid messageId, BloodMoneyChallengeId challengeId, long sequence,
        PlayerId senderPlayerId, Guid clientMessageId, string body, DateTimeOffset createdAt, BloodMoneyChatVisibility visibility)
    {
        if (messageId == Guid.Empty || challengeId.Value == Guid.Empty || senderPlayerId.Value == Guid.Empty ||
            clientMessageId == Guid.Empty || sequence <= 0 || !Enum.IsDefined(visibility) || createdAt.Offset != TimeSpan.Zero ||
            !string.Equals(body, BloodMoneyChatText.Normalize(body), StringComparison.Ordinal))
            throw new InvalidBloodMoneyChatMessageException("Invalid accepted message.");
        MessageId = messageId; ChallengeId = challengeId; Sequence = sequence; SenderPlayerId = senderPlayerId;
        ClientMessageId = clientMessageId; Body = body; CreatedAt = createdAt; Visibility = visibility;
    }
}
