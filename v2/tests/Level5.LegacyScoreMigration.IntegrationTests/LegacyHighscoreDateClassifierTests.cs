using Level5.Application.Migration;

namespace Level5.LegacyScoreMigration.IntegrationTests;

/// <summary>Pure unit tests - no database needed, no locale/timezone dependency (this is exactly the property under test).</summary>
public sealed class LegacyHighscoreDateClassifierTests
{
    [Theory]
    [InlineData("2026-09-25T13:23:45Z")]
    [InlineData("2026-09-25T13:23:45+00:00")]
    [InlineData("2026-09-25T08:23:45-05:00")]
    public void An_explicit_offset_or_utc_marker_classifies_as_offset_aware(string date)
    {
        Assert.Equal(LegacyDateClassification.OffsetAware, LegacyHighscoreDateClassifier.Classify(date));
    }

    [Theory]
    [InlineData("9/25/2026 1:23:45 PM")] // Unity's actual DateTime.Now.ToString() shape (en-US default culture)
    [InlineData("2026-09-25T13:23:45")] // ISO-shaped but with no offset
    public void An_offsetless_but_structurally_parseable_date_classifies_as_ambiguous(string date)
    {
        Assert.Equal(LegacyDateClassification.AmbiguousOrOffsetless, LegacyHighscoreDateClassifier.Classify(date));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date at all")]
    // Only parseable under a day/month/year-first culture, e.g. en-GB - the classifier deliberately
    // parses with CultureInfo.InvariantCulture only (never tries multiple cultures to see what
    // sticks, since guessing which culture produced a string is exactly the kind of invented
    // interpretation this migration must avoid), so this is honestly Unparseable, not "ambiguous".
    [InlineData("25/09/2026 13:23:45")]
    public void A_missing_or_unparseable_date_classifies_as_unparseable(string? date)
    {
        Assert.Equal(LegacyDateClassification.Unparseable, LegacyHighscoreDateClassifier.Classify(date));
    }

    [Fact]
    public void Classification_never_depends_on_the_process_default_culture()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var result = LegacyHighscoreDateClassifier.Classify("9/25/2026 1:23:45 PM");

            Assert.Equal(LegacyDateClassification.AmbiguousOrOffsetless, result);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
