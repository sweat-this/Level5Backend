using Level5.Domain.Common;

namespace Level5.Domain.Competition;

/// <summary>
/// The competitive ruleset for a <see cref="VersusSeries"/>, captured once at creation and
/// frozen for the series' lifetime - later changes to how new series are configured must never
/// retroactively affect a series already in progress.
/// </summary>
public sealed class SeriesFormat
{
    public int TotalGames { get; }
    public int GamesToWin { get; }

    private SeriesFormat(int totalGames)
    {
        TotalGames = totalGames;
        GamesToWin = (totalGames / 2) + 1;
    }

    public static SeriesFormat BestOf(int totalGames)
    {
        if (totalGames < 1 || totalGames > 25)
        {
            throw new InvalidSeriesFormatException("Best-of series length must be between 1 and 25 games.");
        }

        if (totalGames % 2 == 0)
        {
            throw new InvalidSeriesFormatException("Best-of series length must be odd so a series cannot end in a tie.");
        }

        return new SeriesFormat(totalGames);
    }

    public static SeriesFormat Rehydrate(int totalGames) => new(totalGames);
}

public sealed class InvalidSeriesFormatException : DomainException
{
    public InvalidSeriesFormatException(string message) : base(message)
    {
    }
}
