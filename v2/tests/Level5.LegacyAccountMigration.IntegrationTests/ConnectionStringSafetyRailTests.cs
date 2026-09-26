using Level5.Infrastructure.Safety;

namespace Level5.LegacyAccountMigration.IntegrationTests;

public sealed class ConnectionStringSafetyRailTests
{
    private const string LegacyConnectionString = "Host=127.0.0.1;Database=level5;Username=level5;Password=x";

    [Fact]
    public void Default_allow_listed_v2_database_passes()
    {
        var result = ConnectionStringSafetyRail.Check(
            "Host=127.0.0.1;Database=level5_v2;Username=level5;Password=x", LegacyConnectionString, []);

        Assert.True(result.Passed);
    }

    [Fact]
    public void Non_allow_listed_v2_database_is_refused()
    {
        var result = ConnectionStringSafetyRail.Check(
            "Host=127.0.0.1;Database=level5_v2_prod;Username=level5;Password=x", LegacyConnectionString, []);

        Assert.False(result.Passed);
        Assert.Contains("not in the allow-list", result.RefusalReason);
    }

    [Fact]
    public void Explicit_allow_database_widens_the_list()
    {
        var result = ConnectionStringSafetyRail.Check(
            "Host=127.0.0.1;Database=level5_v2_staging;Username=level5;Password=x", LegacyConnectionString, ["level5_v2_staging"]);

        Assert.True(result.Passed);
    }

    [Fact]
    public void Identical_v1_and_v2_database_names_are_refused_even_if_allow_listed()
    {
        var result = ConnectionStringSafetyRail.Check(
            "Host=127.0.0.1;Database=level5_v2;Username=level5;Password=x",
            "Host=127.0.0.1;Database=level5_v2;Username=level5;Password=x",
            []);

        Assert.False(result.Passed);
        Assert.Contains("both point at database", result.RefusalReason);
    }
}
