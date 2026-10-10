using Level5.Application.BloodMoney;
using Level5.Domain.BloodMoney;
using Level5.Infrastructure.BloodMoney;
using System.Security.Cryptography;
using System.Text;

namespace Level5.Infrastructure.IntegrationTests;

public sealed class BloodMoneyChatCursorTests
{
    private const string Secret = "test-only-shared-server-secret-at-least-32-bytes";
    private readonly BloodMoneyChallengeId _id = new(Guid.NewGuid());

    [Theory]
    [InlineData(BloodMoneyChatDirection.Older)]
    [InlineData(BloodMoneyChatDirection.Resume)]
    public void Round_trip_is_stable_across_instances_and_retains_int64_bounds(BloodMoneyChatDirection direction)
    {
        var cursor = new BloodMoneyChatCursor(direction, _id, 9007199254740993, long.MaxValue);
        var token = new HmacBloodMoneyChatCursorCodec(Secret).Encode(cursor);
        Assert.InRange(token.Length, 1, 1024);
        Assert.All(token, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        Assert.Equal(cursor, new HmacBloodMoneyChatCursorCodec(Secret).Decode(token, _id));
    }

    [Fact]
    public void Every_changed_byte_including_version_direction_and_MAC_is_rejected()
    {
        var codec = new HmacBloodMoneyChatCursorCodec(Secret);
        var token = codec.Encode(new(BloodMoneyChatDirection.Older, _id, 10, 20));
        var bytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/'));
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= 1;
            var tampered = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
            AssertInvalid(() => codec.Decode(tampered, _id));
            bytes[i] ^= 1;
        }
        AssertInvalid(() => codec.Decode(token, new(Guid.NewGuid())));
        AssertInvalid(() => new HmacBloodMoneyChatCursorCodec(Secret + "rotated").Decode(token, _id));
    }

    [Theory]
    [InlineData(0, 2)][InlineData(1, 3)]
    public void Authenticated_but_unsupported_version_or_direction_is_rejected(int offset, byte value)
    {
        var codec = new HmacBloodMoneyChatCursorCodec(Secret);
        var token = codec.Encode(new(BloodMoneyChatDirection.Older, _id, 10, 20));
        var bytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/'));
        bytes[offset] = value;
        var key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), "Level5/BloodMoney/ChatCursor/v1"u8);
        HMACSHA256.HashData(key, bytes.AsSpan(0, 34), bytes.AsSpan(34));
        AssertInvalid(() => codec.Decode(Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_'), _id));
    }

    [Theory]
    [InlineData("")][InlineData("garbage")][InlineData("é")][InlineData("AA==")]
    public void Malformed_inputs_and_overlong_inputs_are_bounded(string token)
    {
        var codec = new HmacBloodMoneyChatCursorCodec(Secret);
        AssertInvalid(() => codec.Decode(token, _id));
        AssertInvalid(() => codec.Decode(new string('A', 1025), _id));
    }

    [Theory]
    [InlineData("")][InlineData("short")]
    public void Missing_or_short_secret_cannot_create_a_codec(string secret)
        => Assert.Throws<ArgumentException>(() => new HmacBloodMoneyChatCursorCodec(secret));

    private static void AssertInvalid(Action action)
        => Assert.Equal("invalid_chat_cursor", Assert.Throws<BloodMoneyChatException>(action).Code);
}
