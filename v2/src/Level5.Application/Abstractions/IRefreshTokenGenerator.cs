namespace Level5.Application.Abstractions;

/// <summary>A freshly minted refresh credential: the raw secret to hand to the client, and the one-way representation that gets persisted (see <see cref="IAuthSessionStore"/>).</summary>
public sealed record GeneratedRefreshToken(string RawValue, string Hash);

/// <summary>
/// Produces high-entropy opaque refresh credentials, and derives the stored lookup
/// representation from a raw credential presented by a client. Deliberately not the same port as
/// <see cref="IPasswordHasher"/>: refresh tokens are already high-entropy random secrets, not
/// low-entropy human input, so they call for a fast one-way hash suitable for exact-match lookup
/// rather than a slow, salted password-hashing algorithm.
/// </summary>
public interface IRefreshTokenGenerator
{
    GeneratedRefreshToken Generate();

    /// <summary>Derives the same one-way representation <see cref="Generate"/> would have produced, for looking up a credential presented by a client.</summary>
    string Hash(string rawValue);
}
