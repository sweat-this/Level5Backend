using System.Globalization;
using System.Text.RegularExpressions;

namespace Level5.Application.Migration;

public enum LegacyDateClassification
{
    OffsetAware,
    AmbiguousOrOffsetless,
    Unparseable
}

/// <summary>
/// Classifies a V1 <c>highscores.date</c> value without ever guessing at its meaning. V1 historically
/// wrote this via Unity's <c>DateTime.Now.ToString()</c> (culture-specific, offsetless, client-local
/// wall-clock time) - there is no way to recover the real occurrence instant from that string alone,
/// and this migration deliberately never tries. This classifier is diagnostic-only: <c>audit</c>
/// reports its result so operators can see how much history has an unusable date, but nothing in
/// this migration ever uses a classified date to populate <see cref="Level5.Domain.Results.MatchResult.CreatedAt"/>
/// (which is always the migration-time instant, exactly like a live submission) or to make any
/// import decision. A row is never blocked on its date classification.
/// </summary>
public static class LegacyHighscoreDateClassifier
{
    // An explicit UTC/offset suffix - "Z", or "+hh:mm"/"-hh:mm" - is the only signal that lets a
    // timestamp be trusted without assuming a timezone. `DateTime.Now.ToString()`'s default
    // culture-formatted output never produces one of these, so a real V1 row is expected to land in
    // AmbiguousOrOffsetless or Unparseable, never OffsetAware - that is the honest, expected result,
    // not a defect to route around.
    private static readonly Regex ExplicitOffsetSuffix = new(@"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.Compiled);

    public static LegacyDateClassification Classify(string? date)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return LegacyDateClassification.Unparseable;
        }

        var trimmed = date.Trim();

        if (ExplicitOffsetSuffix.IsMatch(trimmed)
            && DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return LegacyDateClassification.OffsetAware;
        }

        // Structurally parseable as a plain date/time, just with no way to know which timezone it
        // was written in - never fed to DateTimeOffset.TryParse with AssumeUniversal/AssumeLocal,
        // since either would silently manufacture an offset this migration has no basis for.
        return DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? LegacyDateClassification.AmbiguousOrOffsetless
            : LegacyDateClassification.Unparseable;
    }
}
