using Level5.Domain.Competition;
using Xunit;

namespace Level5.Domain.Tests.Competition;

public class SeriesFormatTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(5, 3)]
    [InlineData(7, 4)]
    public void BestOf_computes_games_to_win(int totalGames, int expectedGamesToWin)
    {
        var format = SeriesFormat.BestOf(totalGames);

        Assert.Equal(totalGames, format.TotalGames);
        Assert.Equal(expectedGamesToWin, format.GamesToWin);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(26)]
    public void BestOf_rejects_invalid_lengths(int totalGames)
    {
        Assert.Throws<InvalidSeriesFormatException>(() => SeriesFormat.BestOf(totalGames));
    }
}
