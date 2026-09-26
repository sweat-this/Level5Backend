using Level5.Application.Migration;
using Level5.Infrastructure.Identity;

namespace Level5.LegacyAccountMigration.IntegrationTests;

/// <summary>
/// Unit tests, no containers. The first test here is the one that would have caught the
/// byte-order bug found during design (Identity writes the v3 header big-endian; an earlier draft
/// of the classifier read it little-endian via BinaryReader.ReadInt32, which would have
/// misclassified every real hash as unrecognized).
/// </summary>
public sealed class LegacyCredentialClassifierTests
{
    private readonly AspNetPasswordHasher _hasher = new();

    [Fact]
    public void Real_password_hasher_output_is_recognized()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        var classification = LegacyCredentialClassifier.TryClassify(hash);

        Assert.Equal(LegacyCredentialKind.RecognizedHash, classification.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_is_unrecognized(string? value)
    {
        Assert.Equal(LegacyCredentialKind.UnrecognizedLegacyCredential, LegacyCredentialClassifier.TryClassify(value).Kind);
    }

    [Theory]
    [InlineData("password123")]
    [InlineData("hunter2")]
    [InlineData("not-valid-base64!!")]
    public void Plain_ascii_plaintext_is_unrecognized(string value)
    {
        Assert.Equal(LegacyCredentialKind.UnrecognizedLegacyCredential, LegacyCredentialClassifier.TryClassify(value).Kind);
    }

    [Fact]
    public void Valid_base64_but_too_short_is_unrecognized()
    {
        var tooShort = Convert.ToBase64String(new byte[5]);

        Assert.Equal(LegacyCredentialKind.UnrecognizedLegacyCredential, LegacyCredentialClassifier.TryClassify(tooShort).Kind);
    }

    [Fact]
    public void Unknown_marker_byte_is_unrecognized()
    {
        var bytes = new byte[20];
        bytes[0] = 0xFF;
        var value = Convert.ToBase64String(bytes);

        Assert.Equal(LegacyCredentialKind.UnrecognizedLegacyCredential, LegacyCredentialClassifier.TryClassify(value).Kind);
    }

    [Fact]
    public void Severely_truncated_real_hash_is_unrecognized()
    {
        // Truncated below the 14-byte minimum (13-byte v3 header + 1 subkey byte) can never parse
        // as a plausible header, unlike a moderately truncated hash: the real PasswordHasher<T>
        // itself tolerates a short-but-structurally-valid subkey (it just fails verification later
        // when the derived key doesn't match) rather than treating it as malformed, so the
        // classifier intentionally mirrors that same tolerance rather than rejecting it.
        var hash = _hasher.Hash("whatever");
        var decoded = Convert.FromBase64String(hash);
        var truncated = Convert.ToBase64String(decoded[..10]);

        Assert.Equal(LegacyCredentialKind.UnrecognizedLegacyCredential, LegacyCredentialClassifier.TryClassify(truncated).Kind);
    }

    [Fact]
    public void Moderately_truncated_real_hash_is_still_structurally_plausible()
    {
        // A hash with an intact header but a shorter-than-original subkey still parses as a valid
        // PasswordHasher<T> payload structurally - it just won't verify against the original
        // password later. This mirrors real PasswordHasher<T>.VerifyHashedPassword's own
        // tolerance: it derives a candidate key of whatever length remains and compares, it
        // doesn't reject a short-but-well-formed subkey as malformed.
        var hash = _hasher.Hash("whatever");
        var decoded = Convert.FromBase64String(hash);
        var truncated = Convert.ToBase64String(decoded[..^10]);

        Assert.Equal(LegacyCredentialKind.RecognizedHash, LegacyCredentialClassifier.TryClassify(truncated).Kind);
    }

    [Fact]
    public void Byte_flipped_real_hash_never_throws()
    {
        var hash = _hasher.Hash("whatever");
        var decoded = Convert.FromBase64String(hash);
        decoded[5] ^= 0xFF; // flip a byte inside the iterCount field
        var flipped = Convert.ToBase64String(decoded);

        var exception = Record.Exception(() => LegacyCredentialClassifier.TryClassify(flipped));

        Assert.Null(exception);
    }

    [Fact]
    public void Legacy_v2_format_length_49_is_recognized_and_wrong_length_is_not()
    {
        var correctLength = new byte[49];
        correctLength[0] = 0x00;
        Assert.Equal(LegacyCredentialKind.RecognizedHash, LegacyCredentialClassifier.TryClassify(Convert.ToBase64String(correctLength)).Kind);

        var wrongLength = new byte[37]; // the byte length an earlier, incorrect draft of this classifier used
        wrongLength[0] = 0x00;
        Assert.Equal(LegacyCredentialKind.UnrecognizedLegacyCredential, LegacyCredentialClassifier.TryClassify(Convert.ToBase64String(wrongLength)).Kind);
    }

    [Fact]
    public void Fuzzing_random_byte_arrays_never_throws()
    {
        var random = new Random(12345);

        for (var i = 0; i < 2000; i++)
        {
            var length = random.Next(0, 80);
            var bytes = new byte[length];
            random.NextBytes(bytes);
            var value = Convert.ToBase64String(bytes);

            var exception = Record.Exception(() => LegacyCredentialClassifier.TryClassify(value));

            Assert.Null(exception);
        }
    }
}
