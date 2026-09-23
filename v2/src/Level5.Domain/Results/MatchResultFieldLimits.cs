namespace Level5.Domain.Results;

/// <summary>
/// The authoritative maximum lengths for <see cref="MatchResult"/>'s bounded client-input strings -
/// the single source both domain validation (<see cref="MatchResult.Submit"/>) and the EF column
/// configuration (<c>Level5V2DbContext</c>) read from, so an over-length value is rejected before
/// persistence (400) instead of surfacing as an unhandled PostgreSQL <c>varchar(n)</c> failure (500).
/// </summary>
public static class MatchResultFieldLimits
{
    public const int CharacterIdMaxLength = 64;
    public const int ClientVersionMaxLength = 32;
    public const int PlatformMaxLength = 32;
}
