using Level5.Domain.Common;
using Level5.Domain.Ids;

namespace Level5.Domain.Results;

/// <summary>
/// An immutable, authenticated record of an ordinary completed match - never mutated after
/// creation, unlike a correspondence <see cref="Level5.Domain.Competition.VersusSeries"/>.
/// <see cref="PlayerId"/> is always server-derived from the authenticated principal; every other
/// field is client-reported gameplay/context data, validated but not independently
/// server-verified (this slice is authoritative over ownership, validation, persistence, and
/// idempotency - not gameplay simulation or anti-cheat).
/// </summary>
public sealed class MatchResult
{
    public MatchResultId Id { get; }
    public PlayerId PlayerId { get; }
    public Guid ClientResultId { get; }
    public int ModeId { get; }
    public int LevelId { get; }
    public string CharacterId { get; }
    public string ClientVersion { get; }
    public string Platform { get; }
    public MatchResultMetrics Metrics { get; }
    public MatchResultModifiers Modifiers { get; }
    public DateTimeOffset CreatedAt { get; }

    private MatchResult(
        MatchResultId id, PlayerId playerId, Guid clientResultId, int modeId, int levelId, string characterId,
        string clientVersion, string platform, MatchResultMetrics metrics, MatchResultModifiers modifiers, DateTimeOffset createdAt)
    {
        Id = id;
        PlayerId = playerId;
        ClientResultId = clientResultId;
        ModeId = modeId;
        LevelId = levelId;
        CharacterId = characterId;
        ClientVersion = clientVersion;
        Platform = platform;
        Metrics = metrics;
        Modifiers = modifiers;
        CreatedAt = createdAt;
    }

    public static MatchResult Submit(
        PlayerId playerId, Guid clientResultId, int modeId, int levelId, string characterId,
        string clientVersion, string platform, MatchResultMetrics metrics, MatchResultModifiers modifiers, DateTimeOffset now)
    {
        if (clientResultId == Guid.Empty)
        {
            throw new InvalidMatchResultException("A clientResultId is required to submit a match result.");
        }

        return new MatchResult(
            MatchResultId.New(), playerId, clientResultId,
            RequirePositive(modeId, "modeId"), RequirePositive(levelId, "levelId"),
            RequireWithinLength(characterId, "characterId", MatchResultFieldLimits.CharacterIdMaxLength),
            RequireWithinLength(clientVersion, "clientVersion", MatchResultFieldLimits.ClientVersionMaxLength),
            RequireWithinLength(platform, "platform", MatchResultFieldLimits.PlatformMaxLength),
            metrics ?? throw new InvalidMatchResultException("Metrics are required."),
            modifiers ?? throw new InvalidMatchResultException("Modifiers are required."),
            now);
    }

    /// <summary>Reconstitutes a result from persisted state. Infrastructure only.</summary>
    public static MatchResult Rehydrate(
        MatchResultId id, PlayerId playerId, Guid clientResultId, int modeId, int levelId, string characterId,
        string clientVersion, string platform, MatchResultMetrics metrics, MatchResultModifiers modifiers, DateTimeOffset createdAt)
        => new(id, playerId, clientResultId, modeId, levelId, characterId, clientVersion, platform, metrics, modifiers, createdAt);

    /// <summary>
    /// The idempotent-replay semantic fingerprint check: true iff every client-controlled
    /// persisted field of a candidate resubmission matches what this result was originally
    /// created with. Never compares <see cref="Id"/> or <see cref="CreatedAt"/> (server-generated),
    /// and assumes the caller already matched on <see cref="PlayerId"/>/<see cref="ClientResultId"/>
    /// via lookup before calling this.
    /// </summary>
    public bool MatchesRequest(
        int modeId, int levelId, string characterId, string clientVersion, string platform,
        MatchResultMetrics metrics, MatchResultModifiers modifiers) =>
        ModeId == modeId
        && LevelId == levelId
        && CharacterId == characterId
        && ClientVersion == clientVersion
        && Platform == platform
        && Metrics.Equals(metrics)
        && Modifiers.Equals(modifiers);

    private static int RequirePositive(int value, string fieldName)
    {
        if (value <= 0)
        {
            throw new InvalidMatchResultException($"'{fieldName}' must be a positive integer.");
        }

        return value;
    }

    private static string RequireWithinLength(string value, string fieldName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidMatchResultException($"'{fieldName}' cannot be empty.");
        }

        if (value.Length > maxLength)
        {
            throw new InvalidMatchResultException($"'{fieldName}' cannot exceed {maxLength} characters.");
        }

        return value;
    }
}

public sealed class InvalidMatchResultException : DomainException
{
    public InvalidMatchResultException(string message) : base(message)
    {
    }
}
