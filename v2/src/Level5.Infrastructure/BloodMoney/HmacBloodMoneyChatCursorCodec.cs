using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Level5.Application.BloodMoney;
using Level5.Domain.BloodMoney;

namespace Level5.Infrastructure.BloodMoney;

/// <summary>V1 fixed-width payload followed by an HMAC-SHA256, encoded as canonical base64url.
/// The host's stable secret is purpose-separated before signing; rotating it invalidates old cursors.</summary>
public sealed class HmacBloodMoneyChatCursorCodec : IBloodMoneyChatCursorCodec
{
    private const int PayloadLength = 34;
    private const int TokenLength = PayloadLength + 32;
    private readonly byte[] _key;

    public HmacBloodMoneyChatCursorCodec(string serverSecret)
    {
        if (string.IsNullOrWhiteSpace(serverSecret) || Encoding.UTF8.GetByteCount(serverSecret) < 32)
            throw new ArgumentException("A stable server secret of at least 32 bytes is required.", nameof(serverSecret));
        _key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(serverSecret), "Level5/BloodMoney/ChatCursor/v1"u8);
    }

    public string Encode(BloodMoneyChatCursor cursor)
    {
        Validate(cursor);
        Span<byte> bytes = stackalloc byte[TokenLength];
        bytes[0] = 1;
        bytes[1] = (byte)cursor.Direction;
        cursor.ChallengeId.Value.TryWriteBytes(bytes[2..18]);
        BinaryPrimitives.WriteInt64BigEndian(bytes[18..26], cursor.BoundarySequence);
        BinaryPrimitives.WriteInt64BigEndian(bytes[26..34], cursor.SnapshotUpperBound);
        HMACSHA256.HashData(_key, bytes[..PayloadLength], bytes[PayloadLength..]);
        return Base64Url(bytes);
    }

    public BloodMoneyChatCursor Decode(string token, BloodMoneyChallengeId challengeId)
    {
        // Bound input before any allocation. No padding, Unicode, alternate encodings or extra fields.
        if (token.Length > 1024 || token.Length != 88 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw Invalid();
        Span<byte> bytes = stackalloc byte[TokenLength];
        if (!Convert.TryFromBase64String(token.Replace('-', '+').Replace('_', '/'), bytes, out var written)
            || written != TokenLength || Base64Url(bytes) != token)
            throw Invalid();
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_key, bytes[..PayloadLength], mac);
        if (!CryptographicOperations.FixedTimeEquals(mac, bytes[PayloadLength..]) || bytes[0] != 1)
            throw Invalid();
        var cursor = new BloodMoneyChatCursor((BloodMoneyChatDirection)bytes[1], new(new Guid(bytes[2..18])),
            BinaryPrimitives.ReadInt64BigEndian(bytes[18..26]), BinaryPrimitives.ReadInt64BigEndian(bytes[26..34]));
        Validate(cursor);
        if (cursor.ChallengeId != challengeId) throw Invalid();
        return cursor;
    }

    private static void Validate(BloodMoneyChatCursor cursor)
    {
        if (cursor.ChallengeId.Value == Guid.Empty || !Enum.IsDefined(cursor.Direction)
            || cursor.BoundarySequence < 0 || cursor.SnapshotUpperBound < 0
            || (cursor.Direction == BloodMoneyChatDirection.Older &&
                (cursor.BoundarySequence == 0 || cursor.BoundarySequence > cursor.SnapshotUpperBound)))
            throw Invalid();
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static BloodMoneyChatException Invalid() => new("invalid_chat_cursor", "Chat cursor is invalid.");
}
