using System.Text.RegularExpressions;
using Level5.Domain.Common;

namespace Level5.Domain.Identity;

/// <summary>The private login handle for an <see cref="Account"/>. Never shown to other players.</summary>
public sealed partial class Username : IEquatable<Username>
{
    public string Value { get; }

    private Username(string value)
    {
        Value = value;
    }

    public static Username Create(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            throw new InvalidUsernameException("Username cannot be empty.");
        }

        var trimmed = rawValue.Trim();

        if (!AllowedFormat().IsMatch(trimmed))
        {
            throw new InvalidUsernameException("Username must be 3-32 characters: letters, digits, underscore, or period.");
        }

        return new Username(trimmed);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.]{3,32}$")]
    private static partial Regex AllowedFormat();

    /// <summary>Canonical form used for uniqueness comparisons - usernames are case-insensitive.</summary>
    public string Canonical => Value.ToUpperInvariant();

    public bool Equals(Username? other) => other is not null && Canonical == other.Canonical;

    public override bool Equals(object? obj) => Equals(obj as Username);

    public override int GetHashCode() => Canonical.GetHashCode();

    public override string ToString() => Value;
}

public sealed class InvalidUsernameException : DomainException
{
    public InvalidUsernameException(string message) : base(message)
    {
    }
}
