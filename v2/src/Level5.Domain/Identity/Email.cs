using System.Text.RegularExpressions;
using Level5.Domain.Common;

namespace Level5.Domain.Identity;

/// <summary>
/// A private email address on an <see cref="Account"/>. Never shown through a public
/// player-facing API. Structurally validated and normalized only - no verification, no MX/DNS
/// checks, no confirmation flow.
/// </summary>
public sealed partial class Email : IEquatable<Email>
{
    private const int MaxLength = 320;

    public string Value { get; }

    private Email(string value)
    {
        Value = value;
    }

    public static Email Create(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            throw new InvalidEmailException("Email cannot be empty.");
        }

        var trimmed = rawValue.Trim();

        if (trimmed.Length > MaxLength)
        {
            throw new InvalidEmailException($"Email cannot exceed {MaxLength} characters.");
        }

        if (!AllowedFormat().IsMatch(trimmed))
        {
            throw new InvalidEmailException("Email must be a valid address (e.g. name@example.com).");
        }

        return new Email(trimmed);
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex AllowedFormat();

    /// <summary>Canonical form used for uniqueness comparisons - emails are case-insensitive.</summary>
    public string Canonical => Value.ToLowerInvariant();

    public bool Equals(Email? other) => other is not null && Canonical == other.Canonical;

    public override bool Equals(object? obj) => Equals(obj as Email);

    public override int GetHashCode() => Canonical.GetHashCode();

    public override string ToString() => Value;
}

public sealed class InvalidEmailException : DomainException
{
    public InvalidEmailException(string message) : base(message)
    {
    }
}
