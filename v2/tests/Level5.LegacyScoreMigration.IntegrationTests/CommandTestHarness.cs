using Level5.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Level5.LegacyScoreMigration.IntegrationTests;

/// <summary>Builds the same real DI container Program.cs builds, pointed at the test V2 container, for command-level end-to-end tests.</summary>
internal static class CommandTestHarness
{
    public static ServiceProvider BuildProvider(string v2ConnectionString, Action<IServiceCollection>? configureOverrides = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = v2ConnectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLevel5Infrastructure(configuration);

        configureOverrides?.Invoke(services);

        return services.BuildServiceProvider();
    }
}
