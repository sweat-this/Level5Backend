using System.Text.RegularExpressions;
using Level5.Domain.Common;

namespace Level5.Domain.Players;

/// <summary>
/// A stable, exact-match handle used to locate another player for friend requests and
/// challenges (e.g. "Patrick#4821"). Case-insensitive; canonicalized to upper-invariant for
/// comparison and storage so lookups are exact-match only - no partial/prefix discovery.
/// </summary>
public sealed partial class PlayerTag : IEquatable<PlayerTag>
{
    public string Value { get; }

    private PlayerTag(string value)
    {
        Value = value;
    }

    public static PlayerTag Create(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            throw new InvalidPlayerTagException("Player tag cannot be empty.");
        }

        var trimmed = rawValue.Trim();

        if (!AllowedFormat().IsMatch(trimmed))
        {
            throw new InvalidPlayerTagException(
                "Player tag must be 2-20 letters/digits/underscores, followed by '#' and a 3-6 digit discriminator (e.g. 'Patrick#4821').");
        }

        return new PlayerTag(trimmed.ToUpperInvariant());
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]{2,20}#[0-9]{3,6}$")]
    private static partial Regex AllowedFormat();

    public bool Equals(PlayerTag? other) => other is not null && Value == other.Value;

    public override bool Equals(object? obj) => Equals(obj as PlayerTag);

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;
}

public sealed class InvalidPlayerTagException : DomainException
{
    public InvalidPlayerTagException(string message) : base(message)
    {
    }
}
