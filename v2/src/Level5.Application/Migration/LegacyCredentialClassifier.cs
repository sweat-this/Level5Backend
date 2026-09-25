using System.Buffers.Binary;

namespace Level5.Application.Migration;

public enum LegacyCredentialKind
{
    RecognizedHash,
    UnrecognizedLegacyCredential
}

public readonly record struct LegacyCredentialClassification(LegacyCredentialKind Kind);

/// <summary>
/// Classifies a V1 <c>users.password</c> value as either a stock ASP.NET Core Identity
/// <c>PasswordHasher&lt;T&gt;</c> payload (safe to copy byte-for-byte into V2's
/// <c>Account.PasswordHash</c>) or an unrecognized legacy credential (pre-hardening plaintext, or
/// anything else that isn't a well-formed hash). Never needs the real plaintext - it only inspects
/// the structure of the stored value, mirroring the header-parsing precondition of
/// <c>PasswordHasher&lt;T&gt;.VerifyHashedPassword</c> without going as far as verifying against a
/// supplied password. Never throws: every failure mode collapses to
/// <see cref="LegacyCredentialKind.UnrecognizedLegacyCredential"/>. Never logs or returns the input
/// value.
///
/// Byte layout mirrors <c>Microsoft.AspNetCore.Identity.PasswordHasher&lt;TUser&gt;</c> exactly:
/// - Marker 0x00 (legacy SHA1 "v2" format): 1 (marker) + 16 (salt) + 32 (subkey - Identity always
///   requests a 256-bit subkey regardless of the PRF's native digest size) = 49 bytes, fixed.
/// - Marker 0x01 (PBKDF2 "v3" format - what this codebase's password hasher actually produces):
///   1 (marker) + 4 (prf) + 4 (iterCount) + 4 (saltLength) = 13 header bytes, then salt then
///   subkey. Identity writes prf/iterCount/saltLength in network byte order (big-endian) via its
///   own WriteNetworkByteOrder helper - NOT platform (little-endian) order, so these must be read
///   with BinaryPrimitives.ReadInt32BigEndian. Reading them as ordinary little-endian ints would
///   misinterpret every legitimate hash's header and misclassify it as unrecognized.
/// </summary>
public static class LegacyCredentialClassifier
{
    private const int V2FormatLength = 1 + 16 + 32;
    private const int V3HeaderLength = 1 + 4 + 4 + 4;

    public static LegacyCredentialClassification TryClassify(string? storedValue)
    {
        if (string.IsNullOrEmpty(storedValue))
        {
            return Unrecognized();
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(storedValue);
        }
        catch (FormatException)
        {
            // Plaintext passwords are, for all practical purposes, never valid base64 (wrong
            // length modulo 4, or characters outside the base64 alphabet) - this is the dominant
            // rejection path for real pre-hardening plaintext rows.
            return Unrecognized();
        }

        if (decoded.Length == 0)
        {
            return Unrecognized();
        }

        try
        {
            return decoded[0] switch
            {
                0x00 => decoded.Length == V2FormatLength ? Recognized() : Unrecognized(),
                0x01 => IsPlausibleV3Body(decoded) ? Recognized() : Unrecognized(),
                _ => Unrecognized()
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            // Defense in depth: the explicit length/bounds checks below should make this
            // unreachable, but classification must never throw uncaught on adversarial or
            // corrupted input, so any unexpected bounds failure still collapses to "unrecognized"
            // rather than propagating.
            return Unrecognized();
        }
    }

    private static bool IsPlausibleV3Body(byte[] decoded)
    {
        if (decoded.Length < V3HeaderLength + 1) // header + at least 1 subkey byte
        {
            return false;
        }

        var prf = BinaryPrimitives.ReadInt32BigEndian(decoded.AsSpan(1, 4));
        if (prf is < 0 or > 2) // KeyDerivationPrf has exactly 3 members: HMACSHA1, HMACSHA256, HMACSHA512
        {
            return false;
        }

        var iterCount = BinaryPrimitives.ReadInt32BigEndian(decoded.AsSpan(5, 4));
        if (iterCount <= 0)
        {
            return false;
        }

        var saltLength = BinaryPrimitives.ReadInt32BigEndian(decoded.AsSpan(9, 4));
        var remainingAfterHeader = decoded.Length - V3HeaderLength;

        // Must leave room for at least 1 subkey byte after the salt.
        return saltLength > 0 && saltLength <= remainingAfterHeader - 1;
    }

    private static LegacyCredentialClassification Recognized() => new(LegacyCredentialKind.RecognizedHash);

    private static LegacyCredentialClassification Unrecognized() => new(LegacyCredentialKind.UnrecognizedLegacyCredential);
}
