using System.Text.RegularExpressions;
using Level5.Domain.Common;

namespace Level5.Domain.Platform;

/// <summary>
/// Stable, opaque identifier for an independently access-controlled product. Values are
/// lowercase machine identifiers; callers must not infer hierarchy or behavior by parsing them.
/// </summary>
public sealed partial class ProductId : IEquatable<ProductId>
{
    public const int MaxLength = 64;

    public string Value { get; }

    private ProductId(string value)
    {
        Value = value;
    }

    public static ProductId Create(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            throw new InvalidProductIdException("Product identifier cannot be empty.");
        }

        var canonical = rawValue.Trim().ToLowerInvariant();
        if (canonical.Length > MaxLength || !AllowedFormat().IsMatch(canonical))
        {
            throw new InvalidProductIdException(
                $"Product identifier must be at most {MaxLength} lowercase ASCII letters, digits, or single hyphens between segments.");
        }

        return new ProductId(canonical);
    }

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex AllowedFormat();

    public bool Equals(ProductId? other) => other is not null && Value == other.Value;

    public override bool Equals(object? obj) => Equals(obj as ProductId);

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Value;
}

public sealed class InvalidProductIdException : DomainException
{
    public override string Code => "invalid_product_id";

    public InvalidProductIdException(string message) : base(message)
    {
    }
}
