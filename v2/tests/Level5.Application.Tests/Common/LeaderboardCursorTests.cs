using Level5.Application.Common;
using Xunit;

namespace Level5.Application.Tests.Common;

public class LeaderboardCursorTests
{
    [Fact]
    public void Encode_then_decode_round_trips_every_field()
    {
        var id = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

        var cursor = LeaderboardCursor.Encode("scope", 123.5, createdAt, id);
        var (rankingValue, decodedCreatedAt, decodedId) = LeaderboardCursor.Decode("scope", cursor);

        Assert.Equal(123.5, rankingValue);
        Assert.Equal(createdAt, decodedCreatedAt);
        Assert.Equal(id, decodedId);
    }

    [Fact]
    public void A_scope_containing_the_field_separator_still_round_trips()
    {
        // Regression: GetLeaderboardUseCase originally built scope strings with ":" in them
        // ("leaderboard:1:true:false:null:null"), which corrupted a left-anchored split of the
        // encoded cursor. The scope is caller-defined and must not be assumed colon-free.
        var id = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        const string scopeWithColons = "leaderboard:1:true:false:null:null";

        var cursor = LeaderboardCursor.Encode(scopeWithColons, 42, createdAt, id);
        var (rankingValue, decodedCreatedAt, decodedId) = LeaderboardCursor.Decode(scopeWithColons, cursor);

        Assert.Equal(42, rankingValue);
        Assert.Equal(createdAt, decodedCreatedAt);
        Assert.Equal(id, decodedId);
    }

    [Fact]
    public void Decoding_against_a_different_scope_is_rejected()
    {
        var cursor = LeaderboardCursor.Encode("scope-a", 10, DateTimeOffset.UtcNow, Guid.NewGuid());

        Assert.Throws<ValidationFailedException>(() => LeaderboardCursor.Decode("scope-b", cursor));
    }

    [Theory]
    [InlineData("not-base64!!!")]
    [InlineData("")]
    public void A_malformed_cursor_is_rejected(string cursor)
    {
        Assert.Throws<ValidationFailedException>(() => LeaderboardCursor.Decode("scope", cursor));
    }

    [Fact]
    public void A_well_formed_base64_payload_with_too_few_fields_is_rejected()
    {
        var cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("scope:onlytwofields"));

        Assert.Throws<ValidationFailedException>(() => LeaderboardCursor.Decode("scope", cursor));
    }

    [Fact]
    public void Negative_ranking_values_round_trip()
    {
        // Never expected in practice (MatchResultMetrics rejects negative values), but the codec
        // itself must not silently corrupt a negative value if one ever reaches it.
        var id = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        var cursor = LeaderboardCursor.Encode("scope", -5.5, createdAt, id);
        var (rankingValue, _, _) = LeaderboardCursor.Decode("scope", cursor);

        Assert.Equal(-5.5, rankingValue);
    }
}
