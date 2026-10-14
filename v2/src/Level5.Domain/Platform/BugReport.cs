using Level5.Domain.Common;
using Level5.Domain.Ids;

namespace Level5.Domain.Platform;

public static class BugReportLimits
{
    public const int Title = 160;
    public const int Description = 8000;
    public const int StepsToReproduce = 8000;
    public const int GameVersion = 64;
}

/// <summary>Immutable normalized plain text. Game keys identify reporting products, not entitlements or modules.</summary>
public sealed record BugReportContent
{
    public string GameKey { get; }
    public string Title { get; }
    public string Description { get; }
    public string? Category { get; }
    public string? StepsToReproduce { get; }
    public string? GameVersion { get; }

    private BugReportContent(string gameKey, string title, string description, string? category,
        string? stepsToReproduce, string? gameVersion)
        => (GameKey, Title, Description, Category, StepsToReproduce, GameVersion) =
            (gameKey, title, description, category, stepsToReproduce, gameVersion);

    public static BugReportContent Create(string? gameKey, string? title, string? description,
        string? category = null, string? stepsToReproduce = null, string? gameVersion = null)
    {
        if (gameKey is not ("level5" or "zromp" or "secret-robot" or "blood-money"))
            throw new InvalidBugReportException("Choose a supported game.");
        var normalizedCategory = Text(category, 32, false, "Category");
        if (normalizedCategory is not (null or "gameplay" or "technical" or "interface" or "other"))
            throw new InvalidBugReportException("Choose a supported category.");
        return new(gameKey, Text(title, BugReportLimits.Title, true, "Title")!,
            Text(description, BugReportLimits.Description, true, "Description")!, normalizedCategory,
            Text(stepsToReproduce, BugReportLimits.StepsToReproduce, false, "Steps to reproduce"),
            Text(gameVersion, BugReportLimits.GameVersion, false, "Game version"));
    }

    private static string? Text(string? value, int maximum, bool required, string field)
    {
        // Bound the raw input as well as normalized content. PostgreSQL text cannot contain NUL.
        if (value is not null && (value.Length > maximum || value.Contains('\0')))
            throw new InvalidBugReportException($"{field} must contain at most {maximum} characters and no NUL.");
        var normalized = value?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (!string.IsNullOrWhiteSpace(normalized)) return normalized;
        if (required) throw new InvalidBugReportException($"{field} is required.");
        return null;
    }
}

public sealed record BugReport(Guid Id, PlayerId ReporterPlayerId, Guid ClientSubmissionId,
    BugReportContent Content, DateTimeOffset CreatedAt);

public sealed class InvalidBugReportException(string message) : DomainException(message)
{
    public override string Code => "invalid_bug_report";
}
