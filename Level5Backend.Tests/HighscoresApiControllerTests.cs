using System.Text.Json;
using Level5Backend.Controllers;
using Level5Backend.Models;
using Microsoft.EntityFrameworkCore;

namespace Level5Backend.Tests;

public class HighscoresApiControllerTests
{
    private static Level5Context CreateContext()
    {
        var options = new DbContextOptionsBuilder<Level5Context>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new Level5Context(options);
    }

    private static Highscore MakeHighscore(int id, int modeid, int totalPoints, int hardcore = 0, int traffic = 0, int sniper = 0, int enemies = 0, float time = 10f, int enemiesKilled = 0)
    {
        return new Highscore
        {
            Id = id,
            Userid = id,
            Username = $"user{id}",
            Scoreid = $"score{id}",
            Modeid = modeid,
            Character = "Dr Blood",
            Level = "Level 1",
            Os = "Windows",
            Version = "1.0",
            Date = "2026-01-01",
            Time = time,
            TotalPoints = totalPoints,
            HardcoreEnabled = hardcore,
            TrafficEnabled = traffic,
            SniperEnabled = sniper,
            EnemiesEnabled = enemies,
            EnemiesKilled = enemiesKilled,
            ConsecutiveShots = totalPoints,
            MaxShotMade = totalPoints,
            TotalDistance = totalPoints,
            Platform = "desktop",
        };
    }

    // Regression test for a bug where skip was hardcoded to `page * 10` instead of `page * take` -
    // requesting page 1 at a page size other than 10 silently returned the wrong slice of results.
    [Fact]
    public async Task GetHighScoreByModeIdForGameDisplayAll_SecondPage_SkipsByRequestedPageSizeNotTen()
    {
        await using var context = CreateContext();
        // modeid 1 maps to the TotalPoints metric (see HighscoresApiController.GetScoreMetric).
        for (int i = 1; i <= 100; i++)
        {
            context.Highscores.Add(MakeHighscore(id: i, modeid: 1, totalPoints: 101 - i));
        }
        await context.SaveChangesAsync();

        var controller = new HighscoresApiController(context);
        var result = await controller.GetHighScoreByModeIdForGameDisplayAll(modeid: 1, page: 1, results: 50);

        var rows = Assert.IsAssignableFrom<IEnumerable<object>>(result.Value).Cast<IDictionary<string, object?>>().ToList();

        // Ordered by TotalPoints descending: page 0 is ranks 1-50 (TotalPoints 100 down to 51),
        // so page 1 (skip = 1 * 50) must start at rank 51 (TotalPoints 50), not rank 11 (the old
        // `skip = page * 10` bug would have started here instead).
        Assert.Equal(50, rows.Count);
        Assert.Equal("50", rows[0]["score"]);
    }

    [Fact]
    public async Task GetHighScoreByModeIdForGameDisplayFiltered_SecondPage_SkipsByRequestedPageSizeNotTen()
    {
        await using var context = CreateContext();
        for (int i = 1; i <= 100; i++)
        {
            context.Highscores.Add(MakeHighscore(id: i, modeid: 1, totalPoints: 101 - i));
        }
        await context.SaveChangesAsync();

        var controller = new HighscoresApiController(context);
        var result = await controller.GetHighScoreByModeIdForGameDisplayFiltered(
            modeid: 1, hardcore: 0, traffic: 0, enemies: 0, sniper: 0, page: 1, results: 50);

        var rows = Assert.IsAssignableFrom<IEnumerable<object>>(result.Value).Cast<IDictionary<string, object?>>().ToList();

        Assert.Equal(50, rows.Count);
        Assert.Equal("50", rows[0]["score"]);
    }

    // The six metric-specific methods this replaced each had a slightly different output shape -
    // this locks in that GetByMetric still reproduces the TotalPoints metric's original shape.
    [Fact]
    public async Task GetByMetric_TotalPoints_IncludesStringTimeAndExtraTotalPointsField()
    {
        await using var context = CreateContext();
        context.Highscores.Add(MakeHighscore(id: 1, modeid: 1, totalPoints: 42, time: 12.5f));
        await context.SaveChangesAsync();

        var controller = new HighscoresApiController(context);
        var result = await controller.GetHighScoreByModeIdForGameDisplayAll(modeid: 1, page: 0, results: 50);
        var row = Assert.IsAssignableFrom<IEnumerable<object>>(result.Value).Cast<IDictionary<string, object?>>().Single();

        Assert.Equal("42", row["score"]);
        Assert.Equal("42", row["totalPoints"]?.ToString());
        Assert.IsType<string>(row["time"]);
        Assert.Equal("12.5", row["time"]);
    }

    // The Time metric is the one case where the original GetByTime method returned a raw numeric
    // Time field (not a string) and no redundant extra field - GetByMetric must still do the same.
    [Fact]
    public async Task GetByMetric_Time_ReturnsRawNumericTimeAndNoExtraField()
    {
        await using var context = CreateContext();
        // modeid 7 maps to the Time metric (see HighscoresApiController.GetScoreMetric).
        context.Highscores.Add(MakeHighscore(id: 1, modeid: 7, totalPoints: 0, time: 12.5f));
        await context.SaveChangesAsync();

        var controller = new HighscoresApiController(context);
        var result = await controller.GetHighScoreByModeIdForGameDisplayAll(modeid: 7, page: 0, results: 50);
        var row = Assert.IsAssignableFrom<IEnumerable<object>>(result.Value).Cast<IDictionary<string, object?>>().Single();

        Assert.False(row.ContainsKey("totalPoints"));
        Assert.IsType<float>(row["time"]);
        Assert.Equal(12.5f, row["time"]);
    }

    // GetByEnemiesKilled's original quirk: for that metric, hardcore == 0 only filters by modeid -
    // any other value applies the rest of the filters too. GetByMetric must preserve this exactly.
    [Fact]
    public async Task GetByMetric_EnemiesKilled_HardcoreZero_IgnoresOtherFilters()
    {
        await using var context = CreateContext();
        // modeid 20 maps to the EnemiesKilled metric (see HighscoresApiController.GetScoreMetric).
        context.Highscores.Add(MakeHighscore(id: 1, modeid: 20, totalPoints: 0, traffic: 0, enemiesKilled: 5));
        await context.SaveChangesAsync();

        var controller = new HighscoresApiController(context);
        // hardcore=0 with traffic=1 requested: the traffic filter must NOT be applied for this metric,
        // so the row (which has traffic=0) should still come back.
        var result = await controller.GetHighScoreByModeIdForGameDisplayFiltered(
            modeid: 20, hardcore: 0, traffic: 1, enemies: 0, sniper: 0, page: 0, results: 50);
        var rows = Assert.IsAssignableFrom<IEnumerable<object>>(result.Value).Cast<IDictionary<string, object?>>().ToList();

        Assert.Single(rows);
    }

    [Fact]
    public async Task GetByMetric_EnemiesKilled_HardcoreNonZero_AppliesOtherFilters()
    {
        await using var context = CreateContext();
        context.Highscores.Add(MakeHighscore(id: 1, modeid: 20, totalPoints: 0, hardcore: 1, traffic: 0, enemiesKilled: 5));
        await context.SaveChangesAsync();

        var controller = new HighscoresApiController(context);
        // hardcore=1 with traffic=1 requested: traffic filter DOES apply for this metric, and the
        // seeded row has traffic=0, so it must be filtered out.
        var result = await controller.GetHighScoreByModeIdForGameDisplayFiltered(
            modeid: 20, hardcore: 1, traffic: 1, enemies: 0, sniper: 0, page: 0, results: 50);
        var rows = Assert.IsAssignableFrom<IEnumerable<object>>(result.Value).Cast<IDictionary<string, object?>>().ToList();

        Assert.Empty(rows);
    }

    // GetHighScoreByPlatform previously had no Skip/Take at all - a single request could return an
    // unbounded result set. Confirms it's now capped the same way GetAllHighscores already is.
    [Fact]
    public async Task GetHighScoreByPlatform_CapsResultsAtTwoHundredPerPage()
    {
        await using var context = CreateContext();
        for (int i = 1; i <= 250; i++)
        {
            context.Highscores.Add(MakeHighscore(id: i, modeid: 1, totalPoints: i));
        }
        await context.SaveChangesAsync();

        var controller = new HighscoresApiController(context);
        var result = await controller.GetHighScoreByPlatform("desktop", page: 0, results: 500);

        var highscores = Assert.IsAssignableFrom<IEnumerable<Highscore>>(result.Value);
        Assert.Equal(200, highscores.Count());
    }

    // GetByMetric builds each row as an IDictionary<string, object?> (ExpandoObject) rather than
    // the six original anonymous types. This matters for wire format specifically because ASP.NET
    // Core's default JSON options (Program.cs never overrides them) set PropertyNamingPolicy to
    // CamelCase, which rewrites anonymous/POCO property names in output but does NOT rewrite
    // dictionary keys - so a dictionary built with PascalCase keys would silently serve PascalCase
    // JSON ("Score") where every existing client expects the camelCase ("score") the original
    // anonymous types produced under that same policy. Serializing with the identical
    // JsonSerializerOptions ASP.NET Core's default MVC JSON formatter uses is what makes this test
    // meaningful - serializing with default (no policy) options would pass either way and prove
    // nothing about the real wire format.
    [Fact]
    public async Task GetByMetric_Row_SerializesWithCamelCaseKeysMatchingOriginalWireFormat()
    {
        await using var context = CreateContext();
        context.Highscores.Add(MakeHighscore(id: 1, modeid: 1, totalPoints: 42, time: 12.5f));
        await context.SaveChangesAsync();

        var controller = new HighscoresApiController(context);
        var result = await controller.GetHighScoreByModeIdForGameDisplayAll(modeid: 1, page: 0, results: 50);
        var rows = Assert.IsAssignableFrom<IEnumerable<object>>(result.Value).ToList();

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string json = JsonSerializer.Serialize(rows, jsonOptions);
        using var document = JsonDocument.Parse(json);
        var row = document.RootElement.EnumerateArray().Single();

        Assert.Equal(JsonValueKind.Object, row.ValueKind);
        Assert.Equal("42", row.GetProperty("score").GetString());
        Assert.Equal("42", row.GetProperty("totalPoints").GetRawText());
        Assert.Equal("Dr Blood", row.GetProperty("character").GetString());
        Assert.Equal("12.5", row.GetProperty("time").GetString());
    }
}
